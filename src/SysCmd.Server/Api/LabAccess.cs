namespace SysCmd.Server.Api;

/// <summary>
/// The authentication seam. There is no auth today — this app is meant for a trusted lab network,
/// and the config files hold SNMP community strings and MP passwords in the clear anyway. When
/// that changes, an API key check or cookie scheme goes here and every protected route picks it up
/// without further edits.
/// </summary>
public static class LabAccess
{
    public static RouteGroupBuilder RequireLabAccess(this RouteGroupBuilder group) => group;

    public static IEndpointConventionBuilder RequireLabAccess(this IEndpointConventionBuilder builder) => builder;
}
