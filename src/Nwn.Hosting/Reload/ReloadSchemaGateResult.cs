namespace Nwn.Hosting.Reload;

/// <summary>Result of an optional schema-gate callback passed to <see cref="ReloadHost.ReloadAsync"/>.</summary>
public sealed record ReloadSchemaGateResult(bool Compatible, string Detail)
{
    public static ReloadSchemaGateResult Ok(string detail) => new(true, detail);
    public static ReloadSchemaGateResult Incompatible(string detail) => new(false, detail);
}
