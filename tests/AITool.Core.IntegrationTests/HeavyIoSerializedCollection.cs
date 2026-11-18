using Xunit;

namespace AITool.Core.IntegrationTests;

/// <summary>
/// 重 IO/计时敏感测试串行化：真实 SQLite 写锁窗口与全量套件并行磁盘竞争会把
/// busy-timeout 放大成假失败（单跑全绿、全量偶发），按仓库既有 RelaySerialized 模式收口。
/// </summary>
[CollectionDefinition("HeavyIoSerialized", DisableParallelization = true)]
public sealed class HeavyIoSerializedCollection;
