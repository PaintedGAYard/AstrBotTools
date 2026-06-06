using System.Management.Automation;
using System.Text.Json;

namespace AstrBotTools;

[Cmdlet(VerbsCommon.Get, "AstrBotKnowledgeBaseList")]
[Alias("Get-KBList")]
[OutputType(typeof(PSObject))]
public class GetAstrBotKnowledgeBaseList : PSCmdlet, IDisposable
{
    [Parameter(Mandatory = true, Position = 0)]
    [ValidateNotNullOrEmpty]
    public string BaseUrl { get; set; } = string.Empty;

    [Parameter(Mandatory = true)]
    [ValidateNotNullOrEmpty]
    public string AuthToken { get; set; } = string.Empty;

    private HttpClient _httpClient = null!;

    protected override void BeginProcessing()
    {
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _httpClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", AuthToken);
    }

    protected override void ProcessRecord()
    {
        try
        {
            var url = $"{BaseUrl.TrimEnd('/')}/api/kb/list";
            var response = _httpClient.GetAsync(url).GetAwaiter().GetResult();
            response.EnsureSuccessStatusCode();

            var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            // ★ 修正: data.data.items 才是数组 ★
            if (root.TryGetProperty("data", out var data) &&
                data.TryGetProperty("items", out var items) &&
                items.ValueKind == JsonValueKind.Array)
            {
                foreach (var psObj in KnowledgeBaseInfo.FromJsonArray(items.EnumerateArray()))
                {
                    WriteObject(psObj);
                }
            }
            else
            {
                WriteError(new ErrorRecord(
                    new InvalidOperationException(
                        $"响应中未找到 data.items 数组: {body}"),
                    "KB_LIST_MISSING_DATA",
                    ErrorCategory.InvalidData,
                    body));
            }
        }
        catch (HttpRequestException ex)
        {
            WriteError(new ErrorRecord(ex, "KB_LIST_NETWORK_ERROR",
                ErrorCategory.ConnectionError, BaseUrl));
        }
        catch (JsonException ex)
        {
            WriteError(new ErrorRecord(ex, "KB_LIST_PARSE_ERROR",
                ErrorCategory.ParserError, null));
        }
    }

    public void Dispose()
    {
        _httpClient?.Dispose();
        GC.SuppressFinalize(this);
    }
}
