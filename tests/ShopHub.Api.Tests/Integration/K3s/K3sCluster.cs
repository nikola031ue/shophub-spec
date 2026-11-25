using System.Collections.Concurrent;
using System.Text;
using k8s;
using k8s.Models;
using Testcontainers.K3s;

namespace ShopHub.Api.Tests.Integration.K3s;

/// <summary>
/// A real API server (k3s, same Kubernetes minor as Docker Desktop) with the shop-operator CRDs from
/// the pinned chart, namespaces <c>shops</c>, <c>shophub</c> and <c>elsewhere</c>, and ShopHub's
/// ServiceAccount bound to the delivery.md Role. No operator runs: statuses are set by the tests.
/// </summary>
public sealed class K3sCluster : IAsyncLifetime
{
    public const string Image = "rancher/k3s:v1.36.1-k3s1";
    public const string ShopsNamespace = "shops";
    public const string ShopHubNamespace = "shophub";
    public const string OtherNamespace = "elsewhere";
    public const string ServiceAccount = "shophub";
    public const string User = $"system:serviceaccount:{ShopHubNamespace}:{ServiceAccount}";

    private readonly K3sContainer _container = new K3sBuilder(Image).Build();
    private KubernetesClientConfiguration _admin = null!;
    private string _token = "";

    /// <summary>Cluster admin, for setting up and inspecting.</summary>
    public IKubernetes Admin { get; private set; } = null!;

    private static string Manifest(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Integration", "K3s", "Manifests", name));

    /// <summary>The non-empty YAML documents of a manifest (the CRD file starts with comment-only documents).</summary>
    private static IEnumerable<string> Documents(string name) =>
        System.Text.RegularExpressions.Regex.Split(Manifest(name), @"^---\s*$", System.Text.RegularExpressions.RegexOptions.Multiline)
            .Where(d => d.Split('\n').Any(l => l.Trim().Length > 0 && !l.TrimStart().StartsWith('#')));

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        var kubeconfig = await _container.GetKubeconfigAsync();
        _admin = KubernetesClientConfiguration.BuildConfigFromConfigFile(new MemoryStream(Encoding.UTF8.GetBytes(kubeconfig)));
        Admin = new Kubernetes(_admin);

        foreach (var crd in Documents("shop-operator-crds.yaml").Select(d => KubernetesYaml.Deserialize<V1CustomResourceDefinition>(d)))
        {
            await Admin.ApiextensionsV1.CreateCustomResourceDefinitionAsync(crd);
        }

        foreach (var ns in new[] { ShopsNamespace, ShopHubNamespace, OtherNamespace })
        {
            await Admin.CoreV1.CreateNamespaceAsync(new V1Namespace { Metadata = new V1ObjectMeta { Name = ns } });
        }

        await Admin.CoreV1.CreateNamespacedServiceAccountAsync(
            new V1ServiceAccount { Metadata = new V1ObjectMeta { Name = ServiceAccount } }, ShopHubNamespace);
        foreach (var obj in Documents("shophub-rbac.yaml").Select(d => KubernetesYaml.LoadAllFromString(d).Single()))
        {
            switch (obj)
            {
                case V1Role role:
                    await Admin.RbacAuthorizationV1.CreateNamespacedRoleAsync(role, role.Metadata.NamespaceProperty);
                    break;
                case V1RoleBinding binding:
                    await Admin.RbacAuthorizationV1.CreateNamespacedRoleBindingAsync(binding, binding.Metadata.NamespaceProperty);
                    break;
                default:
                    throw new InvalidOperationException($"Unexpected object in shophub-rbac.yaml: {obj.GetType().Name}");
            }
        }

        var token = await Admin.CoreV1.CreateNamespacedServiceAccountTokenAsync(
            new Authenticationv1TokenRequest { Spec = new V1TokenRequestSpec { ExpirationSeconds = 3600 } },
            ServiceAccount, ShopHubNamespace);
        _token = token.Status.Token;

        await WaitForCrdsAsync();
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    /// <summary>A client acting as ShopHub's ServiceAccount; every request is recorded in <paramref name="recorder"/>.</summary>
    public IKubernetes AsShopHub(RequestRecorder? recorder = null)
    {
        var config = new KubernetesClientConfiguration
        {
            Host = _admin.Host,
            AccessToken = _token,
            SslCaCerts = _admin.SslCaCerts,
            SkipTlsVerify = _admin.SkipTlsVerify,
        };
        return recorder is null ? new Kubernetes(config) : new Kubernetes(config, recorder);
    }

    /// <summary>Asks the API server whether ShopHub's ServiceAccount may do this (like <c>kubectl auth can-i --as</c>).</summary>
    public async Task<bool> CanAsync(string verb, string group, string resource, string? ns, string? name = null, string? subresource = null)
    {
        var review = await Admin.AuthorizationV1.CreateSubjectAccessReviewAsync(new V1SubjectAccessReview
        {
            Spec = new V1SubjectAccessReviewSpec
            {
                User = User,
                Groups = ["system:serviceaccounts", $"system:serviceaccounts:{ShopHubNamespace}", "system:authenticated"],
                ResourceAttributes = new V1ResourceAttributes
                {
                    Verb = verb,
                    Group = group,
                    Resource = resource,
                    NamespaceProperty = ns,
                    Name = name,
                    Subresource = subresource,
                },
            },
        });
        return review.Status.Allowed;
    }

    private async Task WaitForCrdsAsync()
    {
        for (var i = 0; i < 60; i++)
        {
            var crds = await Admin.ApiextensionsV1.ListCustomResourceDefinitionAsync();
            var established = crds.Items.Count(c => c.Spec.Group == "shophub.io"
                && c.Status?.Conditions?.Any(x => x.Type == "Established" && x.Status == "True") == true);
            if (established == 3)
            {
                return;
            }

            await Task.Delay(500);
        }

        throw new TimeoutException("The shop-operator CRDs were not established.");
    }
}

/// <summary>Records every HTTP request a client sends (method, path and query).</summary>
public sealed class RequestRecorder : DelegatingHandler
{
    private readonly ConcurrentQueue<(string Method, string PathAndQuery)> _requests = new();

    public IReadOnlyList<(string Method, string PathAndQuery)> Requests => [.. _requests];

    public void Clear() => _requests.Clear();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        _requests.Enqueue((request.Method.Method, request.RequestUri!.PathAndQuery));
        return base.SendAsync(request, cancellationToken);
    }
}
