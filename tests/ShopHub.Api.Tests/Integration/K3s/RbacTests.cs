using System.Net;
using k8s;
using k8s.Autorest;
using k8s.Models;

namespace ShopHub.Api.Tests.Integration.K3s;

/// <summary>
/// SC-007 for ShopHub, asked of the API server (SubjectAccessReview, as <c>kubectl auth can-i --as</c>):
/// ShopHub may write only Shops, Wallets and DiscordChannels, only in <c>shops</c>, and may only
/// <c>get</c> Secrets there.
/// </summary>
[Collection(K3sGroup.Name)]
public sealed class RbacTests(K3sCluster cluster)
{
    private const string Shops = K3sCluster.ShopsNamespace;

    public static TheoryData<string, string> OwnKinds() => new()
    {
        { "shophub.io", "shops" }, { "shophub.io", "wallets" }, { "shophub.io", "discordchannels" },
    };

    [Theory]
    [MemberData(nameof(OwnKinds))]
    public async Task WritesItsThreeKindsInShops(string group, string resource)
    {
        foreach (var verb in new[] { "get", "list", "watch", "create", "patch", "delete" })
        {
            Assert.True(await cluster.CanAsync(verb, group, resource, Shops), $"{verb} {resource} in shops");
        }
    }

    [Theory]
    [MemberData(nameof(OwnKinds))]
    public async Task NothingOfItsKindsOutsideShops(string group, string resource)
    {
        foreach (var ns in new[] { K3sCluster.ShopHubNamespace, K3sCluster.OtherNamespace, "default", "kube-system" })
        {
            foreach (var verb in new[] { "get", "list", "create", "patch", "delete" })
            {
                Assert.False(await cluster.CanAsync(verb, group, resource, ns), $"{verb} {resource} in {ns}");
            }
        }

        Assert.False(await cluster.CanAsync("list", group, resource, ns: null), $"list {resource} cluster-wide");
    }

    [Theory]
    [MemberData(nameof(OwnKinds))]
    public async Task CannotChangeStatusOrCollectionDelete(string group, string resource)
    {
        Assert.False(await cluster.CanAsync("patch", group, resource, Shops, subresource: "status"));
        Assert.False(await cluster.CanAsync("update", group, resource, Shops, subresource: "status"));
        Assert.False(await cluster.CanAsync("deletecollection", group, resource, Shops));
        Assert.False(await cluster.CanAsync("update", group, resource, Shops));
    }

    [Fact]
    public async Task SecretsGetByNameInShopsOnly()
    {
        Assert.True(await cluster.CanAsync("get", "", "secrets", Shops, "sabcdefgh2-admin"));

        foreach (var verb in new[] { "list", "watch", "create", "update", "patch", "delete", "deletecollection" })
        {
            Assert.False(await cluster.CanAsync(verb, "", "secrets", Shops), $"{verb} secrets in shops");
        }

        foreach (var ns in new[] { K3sCluster.ShopHubNamespace, K3sCluster.OtherNamespace, "default", "kube-system" })
        {
            Assert.False(await cluster.CanAsync("get", "", "secrets", ns, "anything"), $"get secrets in {ns}");
        }
    }

    /// <summary>
    /// RBAC can't narrow <c>get</c> to names ending in <c>-admin</c>: a known name of another Secret in
    /// <c>shops</c> is allowed too. The gateway is what reads only <c>&lt;id&gt;-admin</c>
    /// (<see cref="GatewayTests.TheOnlySecretTheGatewayReadsIsTheAdminSecret"/>).
    /// </summary>
    [Fact]
    public async Task RbacAloneCannotRestrictSecretNames() =>
        Assert.True(await cluster.CanAsync("get", "", "secrets", Shops, "sabcdefgh2-db-app"));

    [Theory]
    [InlineData("", "pods")]
    [InlineData("", "services")]
    [InlineData("", "configmaps")]
    [InlineData("", "serviceaccounts")]
    [InlineData("apps", "deployments")]
    [InlineData("networking.k8s.io", "ingresses")]
    [InlineData("networking.k8s.io", "networkpolicies")]
    [InlineData("postgresql.cnpg.io", "clusters")]
    [InlineData("redis.redis.opstreelabs.in", "redis")]
    [InlineData("rbac.authorization.k8s.io", "roles")]
    [InlineData("rbac.authorization.k8s.io", "rolebindings")]
    public async Task NothingElseInShops(string group, string resource)
    {
        foreach (var verb in new[] { "get", "list", "create", "patch", "delete" })
        {
            Assert.False(await cluster.CanAsync(verb, group, resource, Shops), $"{verb} {group}/{resource} in shops");
        }
    }

    [Theory]
    [InlineData("", "namespaces")]
    [InlineData("", "nodes")]
    [InlineData("apiextensions.k8s.io", "customresourcedefinitions")]
    [InlineData("rbac.authorization.k8s.io", "clusterroles")]
    [InlineData("rbac.authorization.k8s.io", "clusterrolebindings")]
    public async Task NothingClusterWide(string group, string resource)
    {
        foreach (var verb in new[] { "get", "list", "create", "patch", "delete" })
        {
            Assert.False(await cluster.CanAsync(verb, group, resource, ns: null), $"{verb} {group}/{resource}");
        }
    }

    /// <summary>The same through real calls with ShopHub's token, not only the review.</summary>
    [Fact]
    public async Task RealCallsOutsideTheGrantAreForbidden()
    {
        var shophub = cluster.AsShopHub();
        var secret = new V1Secret { Metadata = new V1ObjectMeta { Name = "planted" }, StringData = new Dictionary<string, string> { ["k"] = "v" } };
        var deployment = new V1Deployment
        {
            Metadata = new V1ObjectMeta { Name = "planted" },
            Spec = new V1DeploymentSpec
            {
                Selector = new V1LabelSelector { MatchLabels = new Dictionary<string, string> { ["a"] = "b" } },
                Template = new V1PodTemplateSpec
                {
                    Metadata = new V1ObjectMeta { Labels = new Dictionary<string, string> { ["a"] = "b" } },
                    Spec = new V1PodSpec { Containers = [new V1Container { Name = "c", Image = "nginx" }] },
                },
            },
        };

        await AssertForbiddenAsync(() => shophub.CoreV1.CreateNamespacedSecretAsync(secret, Shops));
        await AssertForbiddenAsync(() => shophub.CoreV1.ListNamespacedSecretAsync(Shops));
        await AssertForbiddenAsync(() => shophub.CoreV1.ReadNamespacedSecretAsync("anything", K3sCluster.ShopHubNamespace));
        await AssertForbiddenAsync(() => shophub.AppsV1.CreateNamespacedDeploymentAsync(deployment, Shops));
        await AssertForbiddenAsync(() => shophub.CustomObjects.ListNamespacedCustomObjectAsync(
            "shophub.io", "v1alpha1", K3sCluster.OtherNamespace, "shops"));
    }

    private static async Task AssertForbiddenAsync(Func<Task> call)
    {
        var e = await Assert.ThrowsAsync<HttpOperationException>(call);
        Assert.Equal(HttpStatusCode.Forbidden, e.Response.StatusCode);
    }
}
