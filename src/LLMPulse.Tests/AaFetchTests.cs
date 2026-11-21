using System.Net;
using LLMPulse.Core.Sources;
using LLMPulse.Core.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace LLMPulse.Tests;

/// <summary>실제 AA Free API 응답 모양(snake_case)으로 역직렬화·페이지 넘김을 검증한다.</summary>
public class AaFetchTests
{
    private sealed class FakeKeys : IApiKeyStore
    {
        public string? Get(string source) => "test-key";
        public void Set(string source, string key) { }
        public bool Has(string source) => true;
    }

    private sealed class Pages(params string[] bodies) : HttpMessageHandler
    {
        public List<string> Requested { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken c)
        {
            Requested.Add(r.RequestUri!.Query);
            var i = Requested.Count - 1;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(bodies[i]) });
        }
    }

    private static string Page(int page, bool hasMore, string slug, string name) => """
        {"pagination":{"page":@PAGE,"page_size":1,"total_pages":2,"has_more":@MORE},
         "data":[{"id":"x","name":"@NAME","slug":"@SLUG","release_date":"2026-11-17",
           "model_creator":{"id":"c","name":"OpenAI"},
           "evaluations":{"artificial_analysis_intelligence_index":70.5,"artificial_analysis_coding_index":null},
           "pricing":{"price_1m_input_tokens":2.0,"price_1m_output_tokens":8.0},
           "performance_v3":{"mean_output_tokens_per_second":90.1,"mean_time_to_first_token_seconds":1.2}}]}
        """.Replace("@PAGE", page.ToString()).Replace("@MORE", hasMore ? "true" : "false").Replace("@NAME", name).Replace("@SLUG", slug);

    [Fact]
    public void Map_ReadsDomainIndexes_FromRealJsonShape()
    {
        var json = """{"name":"M","slug":"m","evaluations":{"artificial_analysis_intelligence_index":60,"artificial_analysis_coding_index":null,"artificial_analysis_finance_and_accounting_index":60.9,"artificial_analysis_strategy_and_ops_index":63.3,"artificial_analysis_legal_index":58.7,"artificial_analysis_healthcare_and_medical_index":57,"artificial_analysis_engineering_index":66.6,"artificial_analysis_economics_index":61.1}}""";
        var row = System.Text.Json.JsonSerializer.Deserialize<AaModel>(json, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        var m = ArtificialAnalysisAdapter.Map(row)!;

        double V(string id) => m.Metrics.Single(x => x.MetricId == id).Value;
        Assert.Equal(60.9, V(MetricIds.FinanceIndex));
        Assert.Equal(63.3, V(MetricIds.StrategyIndex));
        Assert.Equal(58.7, V(MetricIds.LegalIndex));
        Assert.Equal(57, V(MetricIds.HealthcareIndex));
        Assert.Equal(66.6, V(MetricIds.EngineeringIndex));
        Assert.Equal(61.1, V(MetricIds.EconomicsIndex));
        Assert.DoesNotContain(m.Metrics, x => x.MetricId == MetricIds.CodingIndex);
    }

    [Fact]
    public async Task Fetch_UsesDiskCache_WithinTtl_AndRefetchesAfter()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aa-cache-{Guid.NewGuid():N}.json");
        try
        {
            var now = DateTimeOffset.UtcNow;
            var cache = new ResponseCache(path, TimeSpan.FromHours(6), () => now);
            var h1 = new Pages(Page(1, false, "a", "Model A"));
            var first = await new ArtificialAnalysisAdapter(new HttpClient(h1), new FakeKeys(), NullLogger<ArtificialAnalysisAdapter>.Instance, cache)
                .FetchAsync(CancellationToken.None);
            Assert.Single(h1.Requested);

            now = now.AddHours(1); // TTL 안: 호출 없이 같은 결과
            var h2 = new Pages();
            var second = await new ArtificialAnalysisAdapter(new HttpClient(h2), new FakeKeys(), NullLogger<ArtificialAnalysisAdapter>.Instance, cache)
                .FetchAsync(CancellationToken.None);
            Assert.Empty(h2.Requested);
            Assert.Equal(first.Models.Select(m => m.CanonicalName), second.Models.Select(m => m.CanonicalName));

            now = now.AddHours(1.5); // 수동 새로고침(TTL 1시간 단축)이면 TTL 6시간 안이어도 다시 호출
            ResponseCache.TtlOverride = TimeSpan.FromHours(1);
            try
            {
                var hm = new Pages(Page(1, false, "a", "Model A"));
                await new ArtificialAnalysisAdapter(new HttpClient(hm), new FakeKeys(), NullLogger<ArtificialAnalysisAdapter>.Instance, cache)
                    .FetchAsync(CancellationToken.None);
                Assert.Single(hm.Requested);
            }
            finally { ResponseCache.TtlOverride = null; }

            now = now.AddHours(7); // TTL 지남: 다시 호출
            var h3 = new Pages(Page(1, false, "a", "Model A"));
            await new ArtificialAnalysisAdapter(new HttpClient(h3), new FakeKeys(), NullLogger<ArtificialAnalysisAdapter>.Instance, cache)
                .FetchAsync(CancellationToken.None);
            Assert.Single(h3.Requested);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Fetch_DoesNotCache_WhenAPageFails()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aa-cache-{Guid.NewGuid():N}.json");
        var cache = new ResponseCache(path, TimeSpan.FromHours(6));
        var handler = new Pages(Page(1, true, "a", "Model A")); // 2쪽 요청 시 예외(범위 밖)
        await Assert.ThrowsAnyAsync<Exception>(() =>
            new ArtificialAnalysisAdapter(new HttpClient(handler), new FakeKeys(), NullLogger<ArtificialAnalysisAdapter>.Instance, cache)
                .FetchAsync(CancellationToken.None));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Fetch_FollowsPagination_AndReadsVendorAndReleaseDate()
    {
        var handler = new Pages(Page(1, true, "a", "Model A"), Page(2, false, "b", "Model B"));
        var adapter = new ArtificialAnalysisAdapter(new HttpClient(handler), new FakeKeys(), NullLogger<ArtificialAnalysisAdapter>.Instance);

        var result = await adapter.FetchAsync(CancellationToken.None);

        Assert.Equal(2, handler.Requested.Count);
        Assert.Equal(["Model A", "Model B"], result.Models.Select(m => m.CanonicalName).ToArray());
        Assert.All(result.Models, m =>
        {
            Assert.Equal("OpenAI", m.Vendor);
            Assert.Equal("2026-11-17", m.ReleaseDate);
        });
    }
}