using System.Security.Permissions;

// We compile against Jotunn's publicized game assemblies, so private game members look public.
// This lets the Mono JIT skip the access checks at runtime (same trick as JotunnModStub).
#pragma warning disable CS0618 // Type or member is obsolete
[assembly: SecurityPermission(SecurityAction.RequestMinimum, SkipVerification = true)]
