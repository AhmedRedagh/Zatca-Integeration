namespace Girix.Zatca.Enums;

public enum ZatcaEnvironment
{
    Sandbox,
    Simulation,
    Production
}

public static class ZatcaEnvironmentExtensions
{
    public static string GetTemplateName(this ZatcaEnvironment env) => env switch
    {
        ZatcaEnvironment.Sandbox    => "TSTZATCA-Code-Signing",
        ZatcaEnvironment.Simulation => "PREZATCA-Code-Signing",
        ZatcaEnvironment.Production => "ZATCA-Code-Signing",
        _                           => "TSTZATCA-Code-Signing"
    };

    public static bool IsSandbox(this ZatcaEnvironment env)    => env == ZatcaEnvironment.Sandbox;
    public static bool IsProduction(this ZatcaEnvironment env) => env == ZatcaEnvironment.Production;

    public static string GetBaseUrl(this ZatcaEnvironment env) => env switch
    {
        ZatcaEnvironment.Production => "https://gw-fatoora.zatca.gov.sa/e-invoicing/core",
        _                           => "https://gw-fatoora.zatca.gov.sa/e-invoicing/developer-portal"
    };
}
