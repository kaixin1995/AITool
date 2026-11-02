using System.Security.Cryptography;
using System.Text;
using AITool.Application.Networking;
using AITool.Application.Proxy;
using AITool.Domain.Proxy;
using AITool.Infrastructure.Persistence;
using AITool.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SqlSugar;

namespace AITool.IntegrationTests.Proxy;

/// <summary>Temporary SQLite only. No server, account probing, transport or production database.</summary>
public sealed class UpstreamReferenceOwnerTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"aitool-reference-{Guid.NewGuid():N}.db");
    private readonly ServiceProvider _services;
    private readonly ISqlSugarClient _client;
    private readonly Clock _clock = new();
    private readonly UpstreamReferenceContext _context = new("client-a", new AccountIdentity("codex", Guid.NewGuid()),
        UpstreamReferenceNamespace.Create("https://upstream.example/v1/responses", "responses"));

    public UpstreamReferenceOwnerTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSqlSugar($"Data Source={_path};Pooling=False");
        _services = services.BuildServiceProvider();
        _client = _services.GetRequiredService<ISqlSugarClient>();
        _client.CodeFirst.InitTables<UpstreamReferenceOwner>();
    }

    [Fact]
    public async Task Supported_original_request_forms_resolve_same_owner_and_namespace()
    {
        await Guard().CommitOutputAsync(_context,
            """{"id":"resp-one","output":[{"id":"item-one","content":[{"id":"not-an-output-item"}]}],"conversation":{"id":"conv-one"}}""", true);
        var result = await Guard().ResolveRequestAsync("client-a",
            """{"previous_response_id":"resp-one","conversation":"conv-one","input":[{"type":"item_reference","id":"item-one"}]}""");
        result.Should().Be(new UpstreamReferenceRestriction(_context.Owner, _context.UpstreamNamespace));
        (await Guard().ResolveRequestAsync("client-a", """{"conversation":{"id":"conv-one"}}"""))!.Owner.Should().Be(_context.Owner);
        _client.Queryable<UpstreamReferenceOwner>().Count().Should().Be(3);
        await Fails(() => Guard().ResolveRequestAsync("client-a", """{"input":[{"type":"item_reference","id":"not-an-output-item"}]}"""), "unknown_reference");
    }

    [Fact]
    public async Task Implicit_item_references_require_known_ownership_and_pin_the_same_owner()
    {
        await Fails(() => Guard().ResolveRequestAsync("client-a", """{"input":[{"id":"unknown"}]}"""), "unknown_reference");
        await Guard().CommitOutputAsync(_context, """{"output":[{"id":"known-implicit"}]}""", true);
        var result = await Guard().ResolveRequestAsync("client-a", """{"input":[{"id":"known-implicit"}]}""");
        result.Should().Be(new UpstreamReferenceRestriction(_context.Owner, _context.UpstreamNamespace));
        await Guard().CommitOutputAsync(_context with { Owner = new("google", Guid.NewGuid()) },
            """{"id":"different-owner"}""", true);
        await Fails(() => Guard().ResolveRequestAsync("client-a",
            """{"previous_response_id":"different-owner","input":[{"id":"known-implicit"}]}"""), "reference_owner_mismatch");
    }

    [Theory]
    [InlineData("{\"input\":[{\"id\":\"ambiguous\",\"content\":\"hello\",\"role\":\"user\"}]}")]
    [InlineData("{\"input\":[{\"id\":\"ambiguous\",\"type\":null}]}")]
    [InlineData("{\"input\":[{\"id\":\"ambiguous\",\"type\":\"message\"}]}")]
    public async Task Ambiguous_id_bearing_inputs_cannot_bypass_reference_resolution(string json)
    {
        await Fails(() => Guard().ResolveRequestAsync("client-a", json), "invalid_reference_shape");
    }

    [Fact]
    public async Task Valid_explicit_message_id_is_not_an_item_reference()
    {
        (await Guard().ResolveRequestAsync("client-a",
            """{"input":[{"type":"message","id":"local-message-id","role":"user","content":"hello"}]}"""))
            .Should().BeNull();
    }

    [Fact]
    public async Task Full_function_and_tool_output_history_with_ids_is_inline_not_an_owner_lookup()
    {
        var result = await Guard().ResolveRequestAsync("client-a", """
            {"input":[
              {"type":"function_call","id":"fc_1","call_id":"call_1","name":"write","arguments":"{\"path\":\"a\"}","status":"completed"},
              {"type":"function_call_output","id":"fco_1","call_id":"call_1","output":"done"},
              {"type":"custom_tool_call","id":"ct_1","call_id":"call_2","name":"patch","input":"patch text"},
              {"type":"custom_tool_call_output","id":"cto_1","call_id":"call_2","output":"done"}
            ]}
            """);
        result.Should().BeNull();
    }

    [Theory]
    [InlineData("{\"type\":\"reasoning\",\"id\":\"rs_1\",\"summary\":[]}")]
    [InlineData("{\"type\":\"reasoning\",\"id\":\"rs_1\",\"summary\":[{\"type\":\"summary_text\",\"text\":\"hmm\"}],\"encrypted_content\":\"opaque-content\"}")]
    [InlineData("{\"type\":\"reasoning\",\"id\":\"rs_1\",\"encrypted_content\":\"opaque-content\"}")]
    [InlineData("{\"type\":\"compaction\",\"id\":\"cmp_1\",\"encrypted_content\":\"opaque-content\"}")]
    public async Task Complete_reasoning_and_compaction_items_are_inline_even_when_ids_are_unknown(string item)
    {
        (await Guard().ResolveRequestAsync("client-a", "{\"input\":[" + item + "]}")).Should().BeNull();
    }

    [Theory]
    [InlineData("{\"type\":\"function_call\",\"id\":\"fc_1\"}")]
    [InlineData("{\"type\":\"reasoning\",\"id\":\"rs_1\"}")]
    [InlineData("{\"type\":\"compaction\",\"id\":\"cmp_1\"}")]
    [InlineData("{\"type\":\"unknown_family\",\"id\":\"anything\",\"content\":\"pretend-inline\"}")]
    [InlineData("{\"type\":\"function_call_output\",\"id\":\"fco_1\",\"call_id\":\"call_1\"}")]
    public async Task Type_and_id_without_valid_inline_payload_cannot_bypass_reference_guard(string item)
    {
        await Fails(() => Guard().ResolveRequestAsync("client-a", "{\"input\":[" + item + "]}"), "invalid_reference_shape");
    }

    [Fact]
    public async Task Inline_history_does_not_hide_explicit_references_or_nested_reference_shapes()
    {
        const string history = """{"type":"reasoning","id":"rs_1","summary":[]}""";
        await Fails(() => Guard().ResolveRequestAsync("client-a", "{\"input\":[" + history
            + ",{\"type\":\"item_reference\",\"id\":\"unknown-explicit\"}]}"), "unknown_reference");
        await Fails(() => Guard().ResolveRequestAsync("client-a", """
            {"input":[{"type":"function_call_output","id":"fco_1","call_id":"call_1",
              "output":[{"type":"item_reference","id":"nested-reference"}]}]}
            """), "invalid_reference_shape");
        await Guard().CommitOutputAsync(_context, """{"output":[{"id":"known-item"}]}""", true);
        var result = await Guard().ResolveRequestAsync("client-a", "{\"input\":[" + history
            + ",{\"type\":\"item_reference\",\"id\":\"known-item\"}]}" );
        result.Should().Be(new UpstreamReferenceRestriction(_context.Owner, _context.UpstreamNamespace));
    }

    [Fact]
    public async Task No_reference_returns_no_restriction_and_null_optionals_are_not_references()
    {
        (await Guard().ResolveRequestAsync("client-a", """{"previous_response_id":null,"conversation":null,"input":"hello"}""")).Should().BeNull();
        (await Guard().ResolveRequestAsync("client-a", """{"input":[{"type":"message","role":"user","content":"hello"}]}""")).Should().BeNull();
    }

    [Theory]
    [InlineData("{\"previous_response_id\":123}")]
    [InlineData("{\"previous_response_id\":\"\"}")]
    [InlineData("{\"conversation\":{\"name\":\"missing-id\"}}")]
    [InlineData("{\"conversation\":[\"unsupported\"]}")]
    [InlineData("{\"input\":{\"type\":\"item_reference\",\"id\":\"x\"}}")]
    [InlineData("{\"input\":[{\"type\":\"item_reference\"}]}")]
    [InlineData("{\"input\":[{\"type\":\"item_reference\",\"id\":\"x\",\"extra\":1}]}")]
    [InlineData("{\"input\":[{\"type\":\"message\",\"content\":[{\"type\":\"item_reference\",\"id\":\"x\"}]}]}")]
    [InlineData("{\"previous_response_id\":\"a\",\"previous_response_id\":\"b\"}")]
    [InlineData("[]")]
    [InlineData("not-json")]
    public async Task Invalid_and_unsupported_shapes_fail_explicitly(string json)
    {
        await Fails(() => Guard().ResolveRequestAsync("client-a", json), "invalid_reference_shape");
    }

    [Fact]
    public async Task Unknown_native_conversation_and_other_client_never_fall_back_to_single_candidate()
    {
        await Fails(() => Guard().ResolveRequestAsync("client-a", """{"conversation":"unknown-client-supplied"}"""), "unknown_reference");
        await Guard().CommitOutputAsync(_context, """{"id":"known"}""", true);
        await Fails(() => Guard().ResolveRequestAsync("client-b", """{"previous_response_id":"known"}"""), "unknown_reference");
    }

    [Fact]
    public async Task Multiple_owners_or_namespaces_cannot_be_combined_and_kind_is_part_of_lookup()
    {
        await Guard().CommitOutputAsync(_context, """{"id":"first"}""", true);
        await Guard().CommitOutputAsync(_context with { Owner = new("google", Guid.NewGuid()) },
            """{"conversation":"second"}""", true);
        await Fails(() => Guard().ResolveRequestAsync("client-a", """{"previous_response_id":"first","conversation":"second"}"""), "reference_owner_mismatch");
        await Guard().CommitOutputAsync(_context with { UpstreamNamespace = UpstreamReferenceNamespace.Create("https://other.example", "responses") },
            """{"output":[{"id":"third"}]}""", true);
        await Fails(() => Guard().ResolveRequestAsync("client-a", """{"previous_response_id":"first","input":[{"type":"item_reference","id":"third"}]}"""), "reference_owner_mismatch");
        await Fails(() => Guard().ResolveRequestAsync("client-a", """{"conversation":"first"}"""), "unknown_reference");
    }

    [Fact]
    public async Task Different_provider_with_same_guid_is_a_conflict_and_same_owner_cannot_clear_it()
    {
        await Guard().CommitOutputAsync(_context, """{"id":"shared"}""", true);
        await Fails(() => Guard().CommitOutputAsync(_context with { Owner = new("google", _context.Owner.AccountId) },
            """{"id":"shared"}""", true), "reference_conflict");
        await Fails(() => Guard().CommitOutputAsync(_context, """{"id":"shared"}""", true), "reference_conflict");
        await Fails(() => Guard().ResolveRequestAsync("client-a", """{"previous_response_id":"shared"}"""), "reference_conflict");
        _client.Queryable<UpstreamReferenceOwner>().ToList().Single().IsConflict.Should().BeTrue();
    }

    [Fact]
    public async Task Concurrent_two_store_writers_cannot_last_win_and_conflict_survives_restart()
    {
        var first = Guard();
        var second = Guard();
        using var gate = new ManualResetEventSlim();
        async Task<string> Commit(UpstreamReferenceGuard guard, UpstreamReferenceContext context)
        {
            return await Task.Run(async () =>
            {
                gate.Wait();
                try { await guard.CommitOutputAsync(context, """{"id":"racing"}""", true); return "committed"; }
                catch (UpstreamReferenceException ex) { return ex.Code; }
            });
        }
        var a = Commit(first, _context);
        var b = Commit(second, _context with { Owner = new("codex", Guid.NewGuid()) });
        gate.Set();
        var results = await Task.WhenAll(a, b);
        results.Should().BeEquivalentTo(new[] { "committed", "reference_conflict" });
        await Fails(() => Guard().ResolveRequestAsync("client-a", """{"previous_response_id":"racing"}"""), "reference_conflict");
        _client.Queryable<UpstreamReferenceOwner>().Count().Should().Be(1);
    }

    [Fact]
    public async Task Concurrent_same_owner_is_idempotent_but_different_namespace_is_not()
    {
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
            Guard().CommitOutputAsync(_context, """{"id":"same-owner"}""", true))));
        _client.Queryable<UpstreamReferenceOwner>().Count().Should().Be(1);
        await Fails(() => Guard().CommitOutputAsync(_context with
        {
            UpstreamNamespace = UpstreamReferenceNamespace.Create("https://upstream.example", "chat-completions")
        }, """{"id":"same-owner"}""", true), "reference_conflict");
    }

    [Fact]
    public async Task Expired_cleanup_is_bounded_and_remaining_expired_rows_still_fail_resolution()
    {
        var store = new UpstreamReferenceOwnerStore(_services.GetRequiredService<IServiceScopeFactory>(), _clock);
        var references = Enumerable.Range(0, 200).Select(i => new UpstreamReferenceLookup(UpstreamReferenceKind.Response,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"expired-{i}"))).ToLowerInvariant())).ToArray();
        await store.CommitAsync(_context, references, true);
        _clock.Now = _clock.Now.AddHours(24);
        await Guard().CommitOutputAsync(_context, """{"id":"cleanup-trigger"}""", true);
        _client.Queryable<UpstreamReferenceOwner>().Count().Should().Be(73);
        var expired = _client.Queryable<UpstreamReferenceOwner>().ToList().First(r => r.ExpiresAtUtcTicks <= _clock.Now.UtcTicks);
        await Fails(() => store.ResolveAsync("client-a", new[] { new UpstreamReferenceLookup(expired.Kind, expired.LookupDigest) }), "unknown_reference");
    }

    [Fact]
    public async Task Fixed_TTL_survives_restart_reads_and_same_owner_writes_do_not_renew()
    {
        var started = _clock.Now;
        await Guard().CommitOutputAsync(_context, """{"id":"expires"}""", true);
        _clock.Now = started.AddHours(23);
        await Guard().CommitOutputAsync(_context, """{"id":"expires"}""", true);
        (await Guard().ResolveRequestAsync("client-a", """{"previous_response_id":"expires"}"""))!.Owner.Should().Be(_context.Owner);
        _client.Queryable<UpstreamReferenceOwner>().ToList().Single().ExpiresAtUtcTicks.Should().Be(started.AddHours(24).UtcTicks);
        _clock.Now = started.AddHours(24);
        await Fails(() => Guard().ResolveRequestAsync("client-a", """{"previous_response_id":"expires"}"""), "unknown_reference");
        var replacement = _context with { Owner = new("site", Guid.NewGuid()) };
        await Guard().CommitOutputAsync(replacement, """{"id":"expires"}""", true);
        (await Guard().ResolveRequestAsync("client-a", """{"previous_response_id":"expires"}"""))!.Owner.Should().Be(replacement.Owner);
    }

    [Fact]
    public async Task Persisted_ownership_resolves_from_independently_recreated_service_provider()
    {
        await Guard().CommitOutputAsync(_context, """{"id":"restart-durable"}""", true);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSqlSugar($"Data Source={_path};Pooling=False");
        using var restarted = services.BuildServiceProvider();
        var guard = new UpstreamReferenceGuard(new UpstreamReferenceOwnerStore(restarted.GetRequiredService<IServiceScopeFactory>(), _clock));
        (await guard.ResolveRequestAsync("client-a", """{"previous_response_id":"restart-durable"}"""))!
            .Should().Be(new UpstreamReferenceRestriction(_context.Owner, _context.UpstreamNamespace));
        _clock.Now = _clock.Now.AddHours(24);
        await Fails(() => guard.ResolveRequestAsync("client-a", """{"previous_response_id":"restart-durable"}"""), "unknown_reference");
    }

    [Fact]
    public async Task Conflict_expires_at_original_TTL_not_a_renewed_TTL()
    {
        var start = _clock.Now;
        await Guard().CommitOutputAsync(_context, """{"id":"conflict-expires"}""", true);
        _clock.Now = start.AddHours(23);
        await Fails(() => Guard().CommitOutputAsync(_context with { Owner = new("site", Guid.NewGuid()) },
            """{"id":"conflict-expires"}""", true), "reference_conflict");
        _clock.Now = start.AddHours(24);
        await Guard().CommitOutputAsync(_context, """{"id":"conflict-expires"}""", true);
        (await Guard().ResolveRequestAsync("client-a", """{"previous_response_id":"conflict-expires"}"""))!.Owner.Should().Be(_context.Owner);
    }

    [Fact]
    public async Task Synthetic_references_require_replay_and_native_synthetic_collision_is_conflict()
    {
        await Guard().CommitOutputAsync(_context, """{"id":"synthetic","conversation":"synthetic-conv","output":[{"id":"synthetic-item"}]}""", false);
        foreach (var json in new[] { """{"previous_response_id":"synthetic"}""", """{"conversation":"synthetic-conv"}""", """{"input":[{"type":"item_reference","id":"synthetic-item"}]}""" })
            await Fails(() => Guard().ResolveRequestAsync("client-a", json), "reference_replay_required");
        await Fails(() => Guard().CommitOutputAsync(_context, """{"id":"synthetic"}""", true), "reference_conflict");
    }

    [Fact]
    public async Task Json_SSE_envelopes_record_only_response_output_items_and_conversation()
    {
        await Guard().CommitOutputAsync(_context, """{"type":"response.created","response":{"id":"stream-response","conversation":{"id":"stream-conv"},"output":[]}}""", true);
        await Guard().CommitOutputAsync(_context, """{"type":"response.output_item.added","item":{"id":"stream-item","content":[{"id":"arbitrary"}]}}""", true);
        await Guard().CommitOutputAsync(_context, """{"type":"response.output_text.delta","id":"not-response-id","delta":"private-text"}""", true);
        var result = await Guard().ResolveRequestAsync("client-a", """{"previous_response_id":"stream-response","conversation":"stream-conv","input":[{"type":"item_reference","id":"stream-item"}]}""");
        result!.Owner.Should().Be(_context.Owner);
        _client.Queryable<UpstreamReferenceOwner>().Count().Should().Be(3);
    }

    [Theory]
    [InlineData("response.created")]
    [InlineData("response.in_progress")]
    [InlineData("response.completed")]
    [InlineData("response.incomplete")]
    [InlineData("response.failed")]
    public async Task Known_flat_lifecycle_events_record_the_response_id_and_pin_followup_owner(string eventType)
    {
        await Fails(() => Guard().ResolveRequestAsync("client-a", """{"previous_response_id":"flat-response"}"""), "unknown_reference");
        await Guard().CommitOutputAsync(_context,
            "{\"type\":\"" + eventType + "\",\"id\":\"flat-response\",\"output\":[]}", true);
        var result = await Guard().ResolveRequestAsync("client-a", """{"previous_response_id":"flat-response"}""");
        result.Should().Be(new UpstreamReferenceRestriction(_context.Owner, _context.UpstreamNamespace));
        _client.Queryable<UpstreamReferenceOwner>().Count().Should().Be(1);
    }

    [Fact]
    public async Task Enveloped_response_id_takes_precedence_over_event_id_and_other_event_ids_are_not_recorded()
    {
        await Guard().CommitOutputAsync(_context,
            """{"type":"response.created","id":"event-correlation-id","response":{"id":"enveloped-response","output":[]}}""", true);
        foreach (var eventType in new[] { "response.output_text.delta", "response.output_item.added", "unknown.event" })
        {
            await Guard().CommitOutputAsync(_context,
                "{\"type\":\"" + eventType + "\",\"id\":\"unrelated-event-id\",\"item\":{},\"delta\":\"text\"}", true);
        }
        (await Guard().ResolveRequestAsync("client-a", """{"previous_response_id":"enveloped-response"}"""))!
            .Owner.Should().Be(_context.Owner);
        await Fails(() => Guard().ResolveRequestAsync("client-a", """{"previous_response_id":"event-correlation-id"}"""), "unknown_reference");
        await Fails(() => Guard().ResolveRequestAsync("client-a", """{"previous_response_id":"unrelated-event-id"}"""), "unknown_reference");
        _client.Queryable<UpstreamReferenceOwner>().Count().Should().Be(1);
    }

    [Fact]
    public async Task Persisted_rows_and_database_contain_no_reference_or_body_plaintext()
    {
        const string secret = "reference-plaintext-must-not-persist-934834";
        await Guard().CommitOutputAsync(_context, $$"""{"id":"{{secret}}","output":[],"metadata":{"id":"arbitrary-secret"},"body":"private-response-text"}""", true);
        var row = _client.Queryable<UpstreamReferenceOwner>().ToList().Single();
        row.LookupDigest.Should().Be(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))).ToLowerInvariant());
        var dump = System.Text.Json.JsonSerializer.Serialize(row);
        dump.Should().NotContain(secret).And.NotContain("private-response-text").And.NotContain("arbitrary-secret");
        foreach (var suffix in new[] { "", "-wal" })
        {
            if (!File.Exists(_path + suffix)) continue;
            // SQLite may retain pooled file handles on Windows; inspect both DB and WAL
            // without requesting an exclusive share mode or disturbing live SQLite state.
            using var stream = new FileStream(_path + suffix, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var bytes = new MemoryStream();
            stream.CopyTo(bytes);
            Encoding.UTF8.GetString(bytes.ToArray()).Should().NotContain(secret).And.NotContain("private-response-text");
        }
    }

    [Fact]
    public async Task Output_is_not_exposed_if_store_fails_and_error_does_not_leak_json_or_sql()
    {
        _client.Ado.ExecuteCommand("DROP TABLE UpstreamReferenceOwners;");
        var exposed = false;
        var exception = await Fails(async () =>
        {
            await Guard().CommitOutputAsync(_context, """{"id":"private-reference","secret":"private-body"}""", true);
            exposed = true;
        }, "reference_store_unavailable");
        exposed.Should().BeFalse();
        exception.InnerException.Should().BeNull();
        exception.ToString().Should().NotContain("private-reference").And.NotContain("private-body").And.NotContain("INSERT");
        await Fails(() => Guard().ResolveRequestAsync("client-a", """{"previous_response_id":"private-reference"}"""), "reference_store_unavailable");
    }

    [Fact]
    public async Task Id_free_event_uses_read_only_health_check_while_another_writer_holds_the_lock()
    {
        using var writer = _client.CopyNew();
        writer.Ado.BeginTran();
        Task? commit = null;
        bool completedWithoutWriterRelease;
        try
        {
            writer.Ado.ExecuteCommand("UPDATE UpstreamReferenceOwners SET IsConflict=IsConflict WHERE 1=0;");
            commit = Task.Run(() => Guard().CommitOutputAsync(_context,
                """{"type":"response.output_text.delta","delta":"text"}""", true));
            completedWithoutWriterRelease = await Task.WhenAny(commit, Task.Delay(TimeSpan.FromSeconds(2))) == commit;
        }
        finally
        {
            writer.Ado.RollbackTran();
        }
        if (commit is not null) await commit;
        completedWithoutWriterRelease.Should().BeTrue("an id-free event must not acquire SQLite's writer lock");
        _client.Queryable<UpstreamReferenceOwner>().Count().Should().Be(0);
    }

    [Fact]
    public async Task Id_free_event_still_fails_closed_if_ownership_table_is_unavailable()
    {
        _client.Ado.ExecuteCommand("DROP TABLE UpstreamReferenceOwners;");
        var exposed = false;
        await Fails(async () =>
        {
            await Guard().CommitOutputAsync(_context, """{"type":"response.output_text.delta","delta":"text"}""", true);
            exposed = true;
        }, "reference_store_unavailable");
        exposed.Should().BeFalse();
    }

    [Fact]
    public async Task Identifier_cleanup_is_rate_limited_and_id_free_events_do_not_clean_up()
    {
        var store = new UpstreamReferenceOwnerStore(_services.GetRequiredService<IServiceScopeFactory>(), _clock);
        var expired = Enumerable.Range(0, 200).Select(i => new UpstreamReferenceOwner
        {
            ClientScope = "other-client", Kind = UpstreamReferenceKind.Response,
            LookupDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"old-{i}"))).ToLowerInvariant(),
            OwnerProviderKey = _context.Owner.ProviderKey, OwnerAccountId = _context.Owner.AccountId,
            UpstreamNamespace = _context.UpstreamNamespace,
            ExpiresAtUtcTicks = _clock.Now.AddMinutes(-1).UtcTicks
        }).ToArray();
        _client.Insertable(expired).ExecuteCommand();
        await store.CommitAsync(_context, Array.Empty<UpstreamReferenceLookup>(), true);
        _client.Queryable<UpstreamReferenceOwner>().Count().Should().Be(200);
        var first = new UpstreamReferenceLookup(UpstreamReferenceKind.Response, new string('a', 64));
        var second = new UpstreamReferenceLookup(UpstreamReferenceKind.Response, new string('b', 64));
        await store.CommitAsync(_context, new[] { first }, true);
        _client.Queryable<UpstreamReferenceOwner>().Count().Should().Be(73);
        await store.CommitAsync(_context, new[] { second }, true);
        _client.Queryable<UpstreamReferenceOwner>().Count().Should().Be(74);
        _clock.Now = _clock.Now.AddMinutes(1);
        await store.CommitAsync(_context, new[] { second }, true);
        _client.Queryable<UpstreamReferenceOwner>().Count().Should().Be(2);
    }

    [Fact]
    public async Task Output_context_cannot_change_resolved_owner_or_namespace_even_without_new_ids()
    {
        var restriction = new UpstreamReferenceRestriction(_context.Owner, _context.UpstreamNamespace);
        await Fails(() => Guard().CommitOutputAsync(_context with { Owner = new("site", Guid.NewGuid()), Restriction = restriction }, "{}", true), "reference_owner_mismatch");
        await Fails(() => Guard().CommitOutputAsync(_context with { UpstreamNamespace = UpstreamReferenceNamespace.Create("https://other.example", "responses"), Restriction = restriction }, "{}", true), "reference_owner_mismatch");
    }

    [Fact]
    public async Task Cancellation_blocks_writes_and_resolution()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        var write = () => Guard().CommitOutputAsync(_context, """{"id":"cancelled"}""", true, source.Token);
        await write.Should().ThrowAsync<OperationCanceledException>();
        var read = () => Guard().ResolveRequestAsync("client-a", "{}", source.Token);
        await read.Should().ThrowAsync<OperationCanceledException>();
        _client.Queryable<UpstreamReferenceOwner>().Count().Should().Be(0);
    }

    [Fact]
    public void Namespace_is_origin_and_protocol_only_and_has_no_credentials()
    {
        UpstreamReferenceNamespace.Create("HTTPS://UPSTREAM.EXAMPLE:443/v1/responses?key=never-store#fragment", "Responses")
            .Should().Be("https://upstream.example|responses");
        UpstreamReferenceNamespace.Create("https://upstream.example:8443/path", "responses").Should().Be("https://upstream.example:8443|responses");
        var action = () => UpstreamReferenceNamespace.Create("https://user:secret@upstream.example", "responses");
        action.Should().Throw<UpstreamReferenceException>();
    }

    private UpstreamReferenceGuard Guard() => new(new UpstreamReferenceOwnerStore(_services.GetRequiredService<IServiceScopeFactory>(), _clock));

    private static async Task<UpstreamReferenceException> Fails(Func<Task> action, string code)
    {
        var result = await action.Should().ThrowAsync<UpstreamReferenceException>();
        result.Which.Code.Should().Be(code);
        return result.Which;
    }

    public void Dispose()
    {
        // This fixture and its restart provider disable pooling, so disposing our own client
        // releases its handles without clearing pools used by parallel integration tests.
        try { _client.Dispose(); }
        finally { _services.Dispose(); }
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try { File.Delete(_path + suffix); break; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // External scanners/SQLite teardown can briefly retain a Windows handle.
                    // This unique temporary fixture file is safe to leave after bounded retries;
                    // cleanup must not replace a runtime assertion result or affect other tests.
                    if (attempt < 2) Thread.Sleep(25);
                }
            }
        }
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
