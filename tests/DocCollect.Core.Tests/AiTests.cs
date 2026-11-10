using System.Net;
using System.Text;
using DocCollect.Core.Ai;
using DocCollect.Shared;

namespace DocCollect.Core.Tests;

public class AiTests
{
    private static ClassificationRequest Req(string contentType = "image/jpeg") =>
        new("carte nationale d'identité", "Pièce d'identité", "cni.jpg", contentType, [1, 2, 3]);

    [Fact]
    public void Parse_extracts_json_even_with_surrounding_text()
    {
        var r = ClassificationPrompt.Parse("Voici : {\"verdict\":\"match\",\"detected_type\":\"CNI\",\"confidence\":1.4,\"comment\":\"ok\"} fin");
        Assert.Equal(AiVerdict.Match, r.Verdict);
        Assert.Equal("CNI", r.DetectedType);
        Assert.Equal(1.0, r.Confidence);
    }

    [Theory]
    [InlineData("", AiVerdict.Error)]
    [InlineData("pas de json", AiVerdict.Uncertain)]
    [InlineData("{cassé", AiVerdict.Uncertain)]
    [InlineData("{\"verdict\":\"autre\"}", AiVerdict.Uncertain)]
    [InlineData("{\"verdict\":\"MISMATCH\"}", AiVerdict.Mismatch)]
    public void Parse_is_tolerant(string text, AiVerdict expected)
    {
        Assert.Equal(expected, ClassificationPrompt.Parse(text).Verdict);
    }

    [Fact]
    public void OpenAi_response_is_parsed_with_usage()
    {
        const string json = """{"choices":[{"message":{"content":"{\"verdict\":\"mismatch\",\"detected_type\":\"facture\",\"confidence\":0.8}"}}],"usage":{"prompt_tokens":812,"completion_tokens":40}}""";
        var r = OpenAiCompatibleClassifier.ParseResponse(json);
        Assert.Equal(AiVerdict.Mismatch, r.Verdict);
        Assert.Equal(812, r.InputTokens);
        Assert.Equal(40, r.OutputTokens);
    }

    [Fact]
    public void Anthropic_response_is_parsed_with_usage()
    {
        const string json = """{"content":[{"type":"text","text":"{\"verdict\":\"match\",\"detected_type\":\"passeport\",\"confidence\":0.95}"}],"usage":{"input_tokens":1500,"output_tokens":30}}""";
        var r = AnthropicClassifier.ParseResponse(json);
        Assert.Equal(AiVerdict.Match, r.Verdict);
        Assert.Equal(1500, r.InputTokens);
    }

    [Fact]
    public void OpenAi_body_uses_image_url_for_images_and_file_for_pdf()
    {
        var c = new OpenAiCompatibleClassifier("m", new AiProviderOptions { Model = "mistral-small-latest", SupportsPdf = true }, new HttpClient());
        var image = c.BuildBody(Req()).ToJsonString();
        Assert.Contains("\"image_url\"", image);
        Assert.Contains("data:image/jpeg;base64,", image);
        Assert.Contains("mistral-small-latest", image);
        var pdf = c.BuildBody(Req("application/pdf")).ToJsonString();
        Assert.Contains("\"file_data\"", pdf);
    }

    [Fact]
    public void Pdf_support_is_opt_in_for_openai_compatible_providers()
    {
        var noPdf = new OpenAiCompatibleClassifier("m", new AiProviderOptions(), new HttpClient());
        Assert.False(noPdf.SupportsContentType("application/pdf"));
        Assert.True(noPdf.SupportsContentType("image/png"));
    }

    [Fact]
    public void Anthropic_body_uses_document_block_for_pdf()
    {
        var c = new AnthropicClassifier("a", new AiProviderOptions { Model = "claude-haiku-5-5" }, new HttpClient());
        var body = c.BuildBody(Req("application/pdf")).ToJsonString();
        Assert.Contains("\"type\":\"document\"", body);
        Assert.Contains("\"media_type\":\"application/pdf\"", body);
    }

    [Fact]
    public async Task OpenAi_classifier_calls_endpoint_with_bearer_and_query()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"choices":[{"message":{"content":"{\"verdict\":\"match\"}"}}]}""", Encoding.UTF8, "application/json")
        });
        var c = new OpenAiCompatibleClassifier("az", new AiProviderOptions
        {
            BaseUrl = "https://exemple.openai.azure.com/openai/deployments/gpt/", ApiKey = "secret",
            AuthHeader = "api-key", QueryString = "api-version=2024-10-21"
        }, new HttpClient(handler));
        var r = await c.ClassifyAsync(Req());
        Assert.Equal(AiVerdict.Match, r.Verdict);
        var sent = handler.Requests.Single().Request;
        Assert.Equal("https://exemple.openai.azure.com/openai/deployments/gpt/chat/completions?api-version=2024-10-21", sent.RequestUri!.ToString());
        Assert.Equal("secret", sent.Headers.GetValues("api-key").Single());
    }

    [Fact]
    public async Task Http_errors_become_error_verdicts()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        var c = new AnthropicClassifier("a", new AiProviderOptions { ApiKey = "k", Model = "m" }, new HttpClient(handler));
        var r = await c.ClassifyAsync(Req());
        Assert.Equal(AiVerdict.Error, r.Verdict);
        Assert.Contains("429", r.Comment);
        Assert.Equal("2023-06-01", handler.Requests.Single().Request.Headers.GetValues("anthropic-version").Single());
    }

    [Fact]
    public void Registry_builds_providers_from_configuration()
    {
        var options = new AiOptions
        {
            Providers =
            {
                ["mistral-eu"] = new AiProviderOptions { Kind = "openai-compatible", DisplayName = "Mistral (UE)", Region = "UE", BaseUrl = "https://api.mistral.ai/v1" },
                ["claude"] = new AiProviderOptions { Kind = "anthropic", Region = "US" },
                ["off"] = new AiProviderOptions { Kind = "anthropic", Enabled = false }
            }
        };
        var reg = new AiClassifierRegistry(options, _ => new HttpClient());
        Assert.IsType<OpenAiCompatibleClassifier>(reg.Find("MISTRAL-EU"));
        Assert.IsType<AnthropicClassifier>(reg.Find("claude"));
        Assert.Null(reg.Find("off"));
        Assert.Null(reg.Find(null));
        var infos = reg.Describe();
        Assert.Equal(2, infos.Count);
        Assert.Contains(infos, i => i.Key == "claude" && i.SupportsPdf);
        Assert.Contains(infos, i => i.Key == "mistral-eu" && !i.SupportsPdf && i.Region == "UE");
    }

    [Fact]
    public void Registry_rejects_unknown_kind()
    {
        var options = new AiOptions { Providers = { ["x"] = new AiProviderOptions { Kind = "inconnu" } } };
        Assert.Throws<InvalidOperationException>(() => new AiClassifierRegistry(options, _ => new HttpClient()));
    }
}
