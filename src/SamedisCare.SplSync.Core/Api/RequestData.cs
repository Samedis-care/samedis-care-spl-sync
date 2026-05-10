using System.IO;
using System.Net;
using RestSharp;

namespace SamedisCare.SplSync.Core.Api;

/// <summary>
/// Authenticated GET/POST/PUT against Samedis API.
/// File-upload helpers handle the data[document] | data[file] | data[image] fallbacks
/// that we observed against /issues/{id}/uploads.
///
/// Adapted from Samedis-care/samedis-care-external-sync `Samedis.cs`.
/// </summary>
public class RequestData
{
    public int StatusCode;
    public HttpStatusCode Status;
    public string LastError = string.Empty;
    public string LastResponseStatus = string.Empty;

    private readonly string _baseUrl;
    private readonly string _token;
    private readonly RestClientOptions _options;
    private readonly HttpSettings _httpSettings;
    private readonly ISyncLog _log;

    public RequestData(string baseUrl, string token, HttpSettings httpSettings, ISyncLog log)
    {
        _baseUrl = baseUrl;
        _token = token;
        _httpSettings = httpSettings;
        _log = log;

        _options = new RestClientOptions(_baseUrl)
        {
            Timeout = TimeSpan.FromSeconds(_httpSettings.TimeoutSeconds)
        };
        if (!_httpSettings.ValidateCertificate)
            _options.RemoteCertificateValidationCallback = (_, _, _, _) => true;

        if (!string.IsNullOrEmpty(_httpSettings.Proxy))
        {
            var proxy = new WebProxy(_httpSettings.Proxy);
            if (!string.IsNullOrEmpty(_httpSettings.ProxyUsername))
                proxy.Credentials = new NetworkCredential(_httpSettings.ProxyUsername, _httpSettings.ProxyPassword);
            _options.Proxy = proxy;
        }
    }

    public string Get(string resource)
    {
        using var client = new RestClient(_options);
        var request = new RestRequest(resource, Method.Get)
            .AddHeader("accept", "application/json")
            .AddHeader("Content-Type", "application/json")
            .AddHeader("Authorization", $"Bearer {_token}");

        var response = client.ExecuteGet(request);
        response = HandleRetry(response, request, client.ExecuteGet);
        Capture(response);
        return response.Content ?? string.Empty;
    }

    public string Post(string resource, string content)
    {
        using var client = new RestClient(_options);
        var request = new RestRequest(resource, Method.Post)
            .AddHeader("accept", "application/json")
            .AddHeader("Content-Type", "application/json")
            .AddHeader("Authorization", $"Bearer {_token}")
            .AddStringBody(content, DataFormat.Json);

        var response = client.ExecutePost(request);
        response = HandleRetry(response, request, client.ExecutePost);
        Capture(response);
        return response.Content ?? string.Empty;
    }

    public string Put(string resource, string id, string content)
    {
        var putResource = resource;
        var queryIndex = putResource.IndexOf('?');
        if (queryIndex >= 0)
        {
            var path = putResource[..queryIndex];
            var query = putResource[queryIndex..];
            putResource = path + "/" + id + query;
        }
        else
        {
            putResource = putResource + "/" + id;
        }

        using var client = new RestClient(_options);
        var request = new RestRequest(putResource, Method.Put)
            .AddHeader("accept", "application/json")
            .AddHeader("Content-Type", "application/json")
            .AddHeader("Authorization", $"Bearer {_token}")
            .AddStringBody(content, DataFormat.Json);

        var response = client.ExecutePut(request);
        response = HandleRetry(response, request, client.ExecutePut);
        Capture(response);
        return response.Content ?? string.Empty;
    }

    /// <summary>
    /// POST /issues/{id}/uploads with field fallbacks: data[document] -> data[file] -> data[image].
    /// Use this for the full Actimed PDF (5.7).
    /// </summary>
    /// <summary>
    /// POST /issues/{id}/uploads — PDF-Anhang. Field-Name 'data[document]' laut Samedis API-Doc
    /// (siehe docs/api/samedis-public.yaml v4_tenant_issue_uploads). Es gibt nur diesen einen
    /// Field-Namen am Endpoint — fuer PDF und PNG identisch. Server validiert Multipart strikt;
    /// 'data[file]' oder 'data[image]' liefern "File cannot be blank".
    /// </summary>
    public string PostIssueDocument(string resource, string filePath, string fileName)
        => PostIssueUpload(resource, filePath, fileName);

    /// <summary>
    /// POST /issues/{id}/uploads — PNG-Wertenachweis. Identisches Verhalten wie <see cref="PostIssueDocument"/>;
    /// Samedis nutzt am Uploads-Endpoint einheitlich 'data[document]', unabhaengig vom MIME-Type.
    /// </summary>
    public string PostIssueImage(string resource, string filePath, string fileName)
        => PostIssueUpload(resource, filePath, fileName);

    /// <summary>
    /// Gemeinsamer Multipart-Upload-Pfad fuer /issues/{id}/uploads.
    /// Field-Name ist 'data[document]', daneben 'data[name]' fuer den Anzeigename — laut
    /// Samedis API-Doc (docs/api/samedis-public.yaml). Funktioniert fuer beide PDF und PNG;
    /// der MIME-Type wird ueber den Content-Type des File-Parts mitgegeben.
    ///
    /// WICHTIG: KEIN expliziter 'Content-Type'-Header — RestSharp setzt
    /// 'multipart/form-data; boundary=...' automatisch korrekt. Manuelles Setzen ohne Boundary
    /// fuehrt zu "File cannot be blank" 400.
    /// </summary>
    private string PostIssueUpload(string resource, string filePath, string fileName)
    {
        using var client = new RestClient(_options);
        var request = new RestRequest(resource, Method.Post)
            .AddHeader("accept", "application/json")
            .AddHeader("Authorization", $"Bearer {_token}")
            .AddParameter("data[name]", fileName)
            .AddFile("data[document]", filePath, GuessContentType(filePath));
        var response = client.ExecutePost(request);
        response = HandleRetry(response, request, client.ExecutePost);
        Capture(response);
        return response.Content ?? string.Empty;
    }

    /// <summary>
    /// Liefert einen MIME-Type fuer eine Datei anhand der Endung. RestSharp.AddFile akzeptiert
    /// einen optionalen ContentType — manche Server (insb. Rails) sind beim Multipart-Parsing
    /// strenger, wenn der File-Part keinen Content-Type hat.
    /// </summary>
    private static string GuessContentType(string filePath)
    {
        var ext = (Path.GetExtension(filePath) ?? string.Empty).ToLowerInvariant();
        return ext switch
        {
            ".pdf"  => "application/pdf",
            ".png"  => "image/png",
            ".jpg"  => "image/jpeg",
            ".jpeg" => "image/jpeg",
            ".gif"  => "image/gif",
            _       => "application/octet-stream"
        };
    }

    private void Capture(RestResponse response)
    {
        Status = response.StatusCode;
        StatusCode = (int)Status;
        LastResponseStatus = response.ResponseStatus.ToString();
        LastError = response.ErrorMessage ?? response.ErrorException?.Message ?? string.Empty;
    }

    /// <summary>Honour Retry-After on 429 once, then bubble up the response.</summary>
    private static RestResponse HandleRetry(RestResponse response, RestRequest request, Func<RestRequest, RestResponse> execute)
    {
        if (response.StatusCode != HttpStatusCode.TooManyRequests)
            return response;

        var retryAfterHeader = response.Headers?.FirstOrDefault(h =>
            h.Name?.Equals("Retry-After", StringComparison.OrdinalIgnoreCase) == true);
        if (retryAfterHeader?.Value != null && int.TryParse(retryAfterHeader.Value.ToString(), out var seconds))
        {
            Thread.Sleep(seconds * 1000);
            return execute(request);
        }
        return response;
    }
}
