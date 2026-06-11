using AITool.Admin.Services;
using FluentAssertions;

namespace AITool.ApplicationTests.CoreRuntime;

/// <summary>
/// 验证 Admin 侧 ack 状态持久化存储的读写行为。
/// 覆盖正常读写、文件缺失、文件损坏等场景。
/// </summary>
public sealed class CoreEventAckStateStoreTests
{
    /// <summary>
    /// ack.meta 文件不存在时，LoadAckedSequenceId 应返回 0，表示从头开始消费。
    /// </summary>
    [Fact]
    public void LoadAckedSequenceId_returns_zero_when_file_missing()
    {
        var path = GetTempAckMetaPath();
        var store = new CoreEventAckStateStore(path, LoggerStub.Create<CoreEventAckStateStore>());

        store.LoadAckedSequenceId().Should().Be(0);
    }

    /// <summary>
    /// Save → Load 往返测试：保存序号后重新加载，应得到相同的值。
    /// </summary>
    [Fact]
    public void Save_and_Load_roundtrips_sequence_id()
    {
        var path = GetTempAckMetaPath();
        var store = new CoreEventAckStateStore(path, LoggerStub.Create<CoreEventAckStateStore>());

        store.SaveAckedSequenceId(42);

        // 创建新实例从同一文件读取，验证持久化
        var store2 = new CoreEventAckStateStore(path, LoggerStub.Create<CoreEventAckStateStore>());
        store2.LoadAckedSequenceId().Should().Be(42);
    }

    /// <summary>
    /// 连续多次保存不同序号，每次都应覆盖前一次的值。
    /// </summary>
    [Fact]
    public void Save_overwrites_previous_value()
    {
        var path = GetTempAckMetaPath();
        var store = new CoreEventAckStateStore(path, LoggerStub.Create<CoreEventAckStateStore>());

        store.SaveAckedSequenceId(10);
        store.SaveAckedSequenceId(20);
        store.SaveAckedSequenceId(30);

        var store2 = new CoreEventAckStateStore(path, LoggerStub.Create<CoreEventAckStateStore>());
        store2.LoadAckedSequenceId().Should().Be(30);
    }

    /// <summary>
    /// ack.meta 文件内容不是有效数字时，LoadAckedSequenceId 应返回 0 而非抛异常。
    /// </summary>
    [Fact]
    public void LoadAckedSequenceId_returns_zero_when_file_corrupt()
    {
        var path = GetTempAckMetaPath();
        // 直接写入损坏内容
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(path, "not-a-number");

        var store = new CoreEventAckStateStore(path, LoggerStub.Create<CoreEventAckStateStore>());
        store.LoadAckedSequenceId().Should().Be(0);
    }

    /// <summary>
    /// ack.meta 文件内容为负数时，应视为无效，返回 0。
    /// </summary>
    [Fact]
    public void LoadAckedSequenceId_returns_zero_when_value_negative()
    {
        var path = GetTempAckMetaPath();
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(path, "-5");

        var store = new CoreEventAckStateStore(path, LoggerStub.Create<CoreEventAckStateStore>());
        store.LoadAckedSequenceId().Should().Be(0);
    }

    /// <summary>
    /// 保存序号 0 是合法的（表示尚未消费任何事件），应正确持久化和恢复。
    /// </summary>
    [Fact]
    public void Save_and_Load_handles_zero_sequence_id()
    {
        var path = GetTempAckMetaPath();
        var store = new CoreEventAckStateStore(path, LoggerStub.Create<CoreEventAckStateStore>());

        // 先保存一个非零值
        store.SaveAckedSequenceId(100);
        // 再保存 0
        store.SaveAckedSequenceId(0);

        var store2 = new CoreEventAckStateStore(path, LoggerStub.Create<CoreEventAckStateStore>());
        store2.LoadAckedSequenceId().Should().Be(0);
    }

    /// <summary>
    /// 保存大序号值（模拟长时间运行后的场景），应正确持久化。
    /// </summary>
    [Fact]
    public void Save_and_Load_handles_large_sequence_id()
    {
        var path = GetTempAckMetaPath();
        var store = new CoreEventAckStateStore(path, LoggerStub.Create<CoreEventAckStateStore>());

        var largeId = 999_999_999_999L;
        store.SaveAckedSequenceId(largeId);

        var store2 = new CoreEventAckStateStore(path, LoggerStub.Create<CoreEventAckStateStore>());
        store2.LoadAckedSequenceId().Should().Be(largeId);
    }

    /// <summary>
    /// 生成独立的临时 ack.meta 文件路径。
    /// 每个测试使用独立目录，避免相互干扰。
    /// </summary>
    private static string GetTempAckMetaPath()
    {
        return Path.Combine(Path.GetTempPath(), $"aitool-test-ackstore-{Guid.NewGuid():N}", "ack.meta");
    }
}
