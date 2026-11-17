using Xunit;

namespace AITool.Admin.IntegrationTests;

/// <summary>
/// SQLite BEGIN IMMEDIATE 竞争类测试串行化：这些用例自带真实写锁等待窗口，
/// 与其他重 IO 用例并行时 busy-timeout 会被磁盘竞争放大成假失败（master 机器负载低未暴露）。
/// </summary>
[CollectionDefinition("NetworkStoreSerialized", DisableParallelization = true)]
public sealed class NetworkStoreSerializedCollection;
