using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;

namespace Jellyfin.Plugin.Sso.E2eTests;

/// <summary>
/// A reverse proxy in front of the Jellyfin container, publishing it under a sub-path over
/// TLS — the deployment most of this plugin's address handling exists for, and the one the
/// suite could not drive a browser through.
///
/// The harness has always been able to walk a prefixed login by hand: an HttpClient sets
/// X-Forwarded-Prefix, strips the prefix from the path and carries the cookie itself. That
/// covers the server's half. It cannot cover the browser's half, because a browser does not
/// set forwarded headers — a proxy does — and two defects lived in exactly that gap: the
/// configuration page fetched its API from the origin, and the login handoff saved a server
/// address without the prefix. Both were visible only to something that ran the page.
///
/// So this is the proxy a real deployment has. nginx terminates TLS with the suite's own
/// certificate, serves /jellyfin/, strips that prefix on the way through, and tells the
/// server about it in the three headers the plugin reads.
/// </summary>
internal sealed class PrefixedProxy : IAsyncDisposable
{
    private static readonly string Image =
        TestImages.Resolve("E2E_NGINX_IMAGE", "nginx:1.27-alpine");

    /// <summary>The sub-path this proxy publishes the server under.</summary>
    internal const string Prefix = "/jellyfin";

    private readonly IContainer _nginx;

    private PrefixedProxy(IContainer nginx, string baseUrl)
    {
        _nginx = nginx;
        BaseUrl = baseUrl;
    }

    /// <summary>
    /// The address a browser uses, prefix included: what the operator would type. Its host
    /// is the name the suite's certificate is issued for, which the browser resolves
    /// through the launch argument the real-provider tests already use.
    /// </summary>
    internal string BaseUrl { get; }

    internal static async Task<PrefixedProxy> StartAsync(
        TestCertificate certificate, string workDirectory, int jellyfinPort)
    {
        var port = TestCertificate.FreePort();
        var directory = Path.Combine(workDirectory, "prefixed-proxy");
        Directory.CreateDirectory(directory);

        var conf = Path.Combine(directory, "nginx.conf");
        await File.WriteAllTextAsync(conf, NginxConf(port, jellyfinPort));

        var nginx = new ContainerBuilder(Image)
            .WithPortBinding(port, port)
            // Jellyfin is published on a port of the host, not on a network this container
            // shares, so the gateway is how it is reached.
            .WithExtraHost("host.docker.internal", "host-gateway")
            .WithBindMount(conf, "/etc/nginx/nginx.conf", AccessMode.ReadOnly)
            .WithBindMount(certificate.CertificatePath, "/etc/nginx/tls.crt", AccessMode.ReadOnly)
            .WithBindMount(certificate.PrivateKeyPath, "/etc/nginx/tls.key", AccessMode.ReadOnly)
            .WithWaitStrategy(
                Wait.ForUnixContainer().UntilMessageIsLogged("start worker process"))
            .Build();

        await nginx.StartAsync();

        return new PrefixedProxy(
            nginx, $"https://{TestCertificate.HostName}:{port}{Prefix}");
    }

    public async ValueTask DisposeAsync() => await _nginx.DisposeAsync();

    internal async Task<string> LogsAsync()
    {
        var logs = await _nginx.GetLogsAsync();
        return $"{logs.Stdout}\n{logs.Stderr}";
    }

    private static string NginxConf(int port, int jellyfinPort) => $$"""
        events {}

        # nginx says it is listening at notice level, and the image's default error log is
        # warn and a file. The wait strategy needs that line, so both change.
        error_log /dev/stdout notice;

        http {
          # The web client's own requests can be large, and a plugin configuration is one
          # POST of everything on the page.
          client_max_body_size 32m;

          server {
            listen {{port}} ssl;
            ssl_certificate     /etc/nginx/tls.crt;
            ssl_certificate_key /etc/nginx/tls.key;

            # The trailing slash on proxy_pass is what strips the prefix: /jellyfin/web/
            # arrives at the server as /web/. That is a proxy's whole job here, and the
            # reason the server has to be told the prefix separately.
            location {{Prefix}}/ {
              proxy_pass http://host.docker.internal:{{jellyfinPort}}/;

              # $http_host, not $host: nginx's $host drops the port, and the server builds
              # the redirect URI it registers with the provider out of this header. With
              # the port missing the provider refused the login — which is what it should
              # do, and the one deployment where it does not show up is a proxy on 443.
              proxy_set_header Host $http_host;
              proxy_set_header X-Forwarded-Host $http_host;
              proxy_set_header X-Forwarded-Proto https;
              proxy_set_header X-Forwarded-Prefix {{Prefix}};
              proxy_set_header X-Real-IP $remote_addr;

              # The web client opens a socket as soon as it has a session.
              proxy_http_version 1.1;
              proxy_set_header Upgrade $http_upgrade;
              proxy_set_header Connection "upgrade";

              proxy_read_timeout 120s;
            }

            # A bare /jellyfin, as somebody would type it.
            location = {{Prefix}} {
              return 302 {{Prefix}}/web/index.html;
            }
          }
        }
        """;
}
