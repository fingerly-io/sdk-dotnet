using System;
using Fingerly.AspNetCore;
using Microsoft.AspNetCore.Http;

namespace Fingerly;

/// <summary>
/// Looks up what sent an ASP.NET Core request.
/// </summary>
public static class AgentSightingsHttpContextExtensions
{
    /// <summary>
    /// What sent this request, as the server answered for the same user agent,
    /// address and <c>Signature-Agent</c>, or <see cref="AgentAnswer.Unknown"/>.
    /// </summary>
    /// <remarks>
    /// In memory only; it never waits on the network and never throws. Treat
    /// <see cref="AgentAnswer.Unknown"/> as "serve as usual".
    /// </remarks>
    /// <param name="sightings">The configured component.</param>
    /// <param name="context">The request being served.</param>
    /// <returns>The remembered answer, or <see cref="AgentAnswer.Unknown"/>.</returns>
    public static AgentAnswer Lookup(this AgentSightings sightings, HttpContext context)
    {
        if (sightings is null || context is null)
        {
            return AgentAnswer.Unknown;
        }
        try
        {
            return sightings.LookupPrepared(SightingsExchange.Prepared(context, sightings));
        }
#pragma warning disable CA1031 // Fail open: a lookup never throws.
        catch (Exception)
#pragma warning restore CA1031
        {
            return AgentAnswer.Unknown;
        }
    }
}
