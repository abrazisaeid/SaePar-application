namespace SaeParTunnel.Core.Models;

public sealed record TestResult(
    bool Success,
    int? LatencyMs,
    string Message,
    ValidationLevel Level = ValidationLevel.FullProxy);
