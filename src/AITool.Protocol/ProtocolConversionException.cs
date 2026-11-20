namespace AITool.Protocol;

/// <summary>A client input cannot be represented safely by the selected upstream protocol.</summary>
public sealed class ProtocolConversionException(string message) : InvalidOperationException(message);
