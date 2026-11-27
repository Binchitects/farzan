// SPDX-License-Identifier: AGPL-3.0-only OR LicenseRef-Farzan-Commercial
// Copyright (C) 2026 Binchitects
using Llm.Api.Access;

namespace Llm.Api.Downloads;

/// <summary>The certificate people's tools must trust on a self-signed deployment (Connect your tools): public, like the web's files.</summary>
public static class CertificateEndpoints
{
    public static void MapCertificate(IEndpointRouteBuilder app)
    {
        var c = app.MapGroup("/api/downloads/certificate").AllowAnonymous("The stack's public CA certificate.");
        c.MapGet("", async (SiteCertificate site, CancellationToken ct) =>
        {
            var i = await site.GetAsync(ct);
            return Results.Ok(new { i.Available, i.Trusted, i.Subject, i.Issuer, i.Expires, i.Sha256 });
        });
        c.MapGet("/ca.crt", async (SiteCertificate site, Microsoft.Extensions.Options.IOptions<Identity.AuthOptions> auth, CancellationToken ct) =>
            await site.GetAsync(ct) is { Pem: { } pem }
                ? Results.File(System.Text.Encoding.ASCII.GetBytes(pem), "application/x-x509-ca-cert", $"{auth.Value.Domain}-ca.crt")
                : Results.NotFound(new { error = "The site's certificate could not be read." }));
    }
}
