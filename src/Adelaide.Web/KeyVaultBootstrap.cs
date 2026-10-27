using Azure.Identity;
using Azure.Security.KeyVault.Secrets;

namespace Adelaide.Web;

/// <summary>Optional Azure Key Vault secret bootstrap. When <c>ADELAIDE_KEYVAULT_URI</c> is set, it loads the configured
/// secrets from the vault — via <see cref="DefaultAzureCredential"/> (a managed identity in the cloud, your az-login on
/// a dev machine) — into process environment variables BEFORE <c>SharePointOptions/AiOptions.Load()</c> read them, so the
/// existing config resolution is unchanged. When the URI is unset this is a no-op and config falls back to <c>.env</c>
/// exactly as before — so the migration is safe and reversible.
///
/// KV secret names use '-' where the env var uses '_' (Key Vault disallows underscores): ENTRA_CLIENT_SECRET maps to the
/// secret ENTRA-CLIENT-SECRET. An env var already present in the process is never overwritten — an explicit export
/// (or .env, read later) wins, which keeps a local/break-glass override possible. No secret value is ever logged.</summary>
public static class KeyVaultBootstrap
{
    // The secrets the app expects to source from the vault. Extend as more move in (the drain's client secret, etc.).
    private static readonly string[] SecretEnvVars = { "ENTRA_CLIENT_SECRET" };

    /// <summary>Loads the secrets (best-effort per secret) and returns a short, NON-PHI summary for the startup banner
    /// (the host has no logger configured this early). A missing/denied secret is left unset so .env fills it — or, if
    /// neither has it, the relevant Load() fails loudly with its own clear "Missing …" message.</summary>
    public static async Task<string> LoadAsync(CancellationToken ct = default)
    {
        var uri = Environment.GetEnvironmentVariable("ADELAIDE_KEYVAULT_URI");
        if (string.IsNullOrWhiteSpace(uri)) return "off (.env/env)";

        var client = new SecretClient(new Uri(uri), new DefaultAzureCredential());
        int loaded = 0, failed = 0;
        foreach (var envName in SecretEnvVars)
        {
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(envName))) continue; // explicit env / .env wins
            try
            {
                var secret = await client.GetSecretAsync(envName.Replace('_', '-'), cancellationToken: ct);
                Environment.SetEnvironmentVariable(envName, secret.Value.Value);
                loaded++;
            }
            catch
            {
                failed++; // missing/denied/unreachable → leave unset; .env fallback (or a loud Missing-… at Load time)
            }
        }
        return failed == 0 ? $"on ({loaded}/{SecretEnvVars.Length} from {uri})" : $"on ({loaded} ok, {failed} failed → .env fallback)";
    }
}
