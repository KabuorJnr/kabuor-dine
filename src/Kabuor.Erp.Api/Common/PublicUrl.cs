// The address the outside world reaches this server on, for links people are sent and for the callbacks Safaricom
// makes. Some of those callbacks carry a secret in the path, so the address can't come from the request's Host
// header: anyone can put any Host on a request, and Safaricom would then hand the secret to their server.
namespace Kabuor.Erp.Common;

public static class PublicUrl
{
    /// <summary>App:PublicUrl if set; else the production domain Vercel gives the project (VERCEL_PROJECT_PRODUCTION_URL,
    /// a custom domain when there is one; previews use it too, so callbacks never land on a protected preview), or the
    /// address Render gives the service (RENDER_EXTERNAL_URL); the request's own host only when none is set, as in
    public static string Of(HttpRequest request)
    {
        if (Configured(request.HttpContext.RequestServices.GetRequiredService<IConfiguration>()) is { } configured)
            return configured;
        if (request.Headers.Origin.FirstOrDefault() is { Length: > 0 } origin && Uri.TryCreate(origin, UriKind.Absolute, out var originUri))
            return $"{originUri.Scheme}://{originUri.Authority}".TrimEnd('/');
        if (request.Headers.Referer.FirstOrDefault() is { Length: > 0 } referer && Uri.TryCreate(referer, UriKind.Absolute, out var refUri))
            return $"{refUri.Scheme}://{refUri.Authority}".TrimEnd('/');
        return $"{request.Scheme}://{request.Host}";
    }

    public static string? Configured(IConfiguration config)
    {
        if (config["App:PublicUrl"] is { Length: > 0 } url) return url.Trim().TrimEnd('/');
        if (config["VERCEL_PROJECT_PRODUCTION_URL"] is { Length: > 0 } vercel) return $"https://{vercel.Trim().TrimEnd('/')}";
        if (config["RENDER_EXTERNAL_URL"] is { Length: > 0 } render) return render.Trim().TrimEnd('/');
        return null;
    }
}
