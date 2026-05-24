using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Girix.Zatca.Exceptions;
using Girix.Zatca.Models;

namespace Girix.Zatca.Services;

/// <summary>
/// Thin HTTP wrapper for all ZATCA Fatoora portal API endpoints.
/// </summary>
internal class ZatcaApiClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string     _baseUrl;

    private const string AcceptVersion = "V2";
    private const string AcceptLanguage = "en";

    public ZatcaApiClient(string baseUrl, HttpClient? httpClient = null)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _http    = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
    }

    // ────────────────────────────────────────────────────────────────────────────
    // Onboarding
    // ────────────────────────────────────────────────────────────────────────────

    public async Task<ZatcaCredentials> FetchComplianceCsidAsync(string otp, string csrPem)
    {
        var csrBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(csrPem));
        var body      = JsonSerializer.Serialize(new { csr = csrBase64 });

        var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/compliance")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        AddCommonHeaders(request);
        request.Headers.Add("OTP", otp);

        var json = await SendAsync(request, "compliance CSID");
        return ParseCredentials(json, isProduction: false);
    }

    public async Task<ZatcaCredentials> FetchProductionCsidAsync(ZatcaCredentials complianceCreds)
    {
        var body    = JsonSerializer.Serialize(new { compliance_request_id = complianceCreds.RequestId });
        var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/production/csids")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        AddCommonHeaders(request);
        AddBasicAuth(request, complianceCreds);

        var json = await SendAsync(request, "production CSID");
        return ParseCredentials(json, isProduction: true);
    }

    // ────────────────────────────────────────────────────────────────────────────
    // Compliance checks
    // ────────────────────────────────────────────────────────────────────────────

    public async Task<ComplianceCheckResponse> RunComplianceCheckAsync(
        string invoiceBase64,
        string invoiceHash,
        string invoiceUuid,
        ZatcaCredentials credentials)
    {
        var body    = JsonSerializer.Serialize(new { invoiceHash, uuid = invoiceUuid, invoice = invoiceBase64 });
        var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/compliance/invoices")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        AddCommonHeaders(request);
        AddBasicAuth(request, credentials);

        var json = await SendAsync(request, "compliance check");
        return ParseComplianceCheck(json);
    }

    // ────────────────────────────────────────────────────────────────────────────
    // Clearance / Reporting
    // ────────────────────────────────────────────────────────────────────────────

    public async Task<ZatcaClearanceResult> ClearInvoiceAsync(
        string invoiceBase64,
        string invoiceHash,
        string invoiceUuid,
        ZatcaCredentials credentials)
    {
        var body    = JsonSerializer.Serialize(new { invoiceHash, uuid = invoiceUuid, invoice = invoiceBase64 });
        var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/invoices/clearance/single")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        AddCommonHeaders(request);
        AddBasicAuth(request, credentials);

        var json = await SendAsync(request, "clearance");
        return ParseClearanceResult(json);
    }

    public async Task<ZatcaReportingResult> ReportInvoiceAsync(
        string invoiceBase64,
        string invoiceHash,
        string invoiceUuid,
        ZatcaCredentials credentials)
    {
        var body    = JsonSerializer.Serialize(new { invoiceHash, uuid = invoiceUuid, invoice = invoiceBase64 });
        var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/invoices/reporting/single")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        AddCommonHeaders(request);
        AddBasicAuth(request, credentials);

        var json = await SendAsync(request, "reporting");
        return ParseReportingResult(json);
    }

    // ────────────────────────────────────────────────────────────────────────────
    // HTTP helpers
    // ────────────────────────────────────────────────────────────────────────────

    private async Task<JsonDocument> SendAsync(HttpRequestMessage request, string operation)
    {
        try
        {
            using var response = await _http.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            // Strip BOM if present
            body = body.TrimStart('﻿').Trim();

            if (!response.IsSuccessStatusCode)
                throw new ZatcaException(
                    $"ZATCA {operation} failed [{(int)response.StatusCode}]: {Truncate(body, 300)}",
                    (int)response.StatusCode);

            try
            {
                return JsonDocument.Parse(body);
            }
            catch (JsonException)
            {
                throw new ZatcaException($"Unexpected ZATCA {operation} response: {Truncate(body, 300)}");
            }
        }
        catch (ZatcaException) { throw; }
        catch (Exception ex)
        {
            throw new ZatcaException($"ZATCA {operation} HTTP error: {ex.Message}", inner: ex);
        }
    }

    private static void AddCommonHeaders(HttpRequestMessage req)
    {
        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
        req.Headers.Add("Accept-Version",  AcceptVersion);
        req.Headers.Add("Accept-Language", AcceptLanguage);
    }

    private static void AddBasicAuth(HttpRequestMessage req, ZatcaCredentials creds)
    {
        var raw     = Encoding.UTF8.GetBytes($"{creds.BinarySecurityToken}:{creds.Secret}");
        var encoded = Convert.ToBase64String(raw);
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", encoded);
    }

    // ────────────────────────────────────────────────────────────────────────────
    // JSON parsers
    // ────────────────────────────────────────────────────────────────────────────

    private static ZatcaCredentials ParseCredentials(JsonDocument doc, bool isProduction)
    {
        var root  = doc.RootElement;
        var token = root.TryGetProperty("binarySecurityToken", out var t) ? t.GetString() ?? "" : "";
        var secret = root.TryGetProperty("secret", out var s) ? s.GetString() ?? "" : "";
        var reqId  = root.TryGetProperty("requestID",          out var r) ? r.ToString() :
                     root.TryGetProperty("requestId",          out var r2) ? r2.ToString() : "";

        return ZatcaCredentials.FromApiResponse(reqId, token, secret, isProduction);
    }

    private static ComplianceCheckResponse ParseComplianceCheck(JsonDocument doc)
    {
        var root = doc.RootElement;
        return new ComplianceCheckResponse
        {
            InvoiceUuid = GetString(root, "invoiceUUID"),
            InvoiceHash = GetString(root, "invoiceHash"),
            Status      = GetString(root, "status"),
            Warnings    = ParseMessages(root, "warningMessages"),
            Errors      = ParseMessages(root, "errorMessages")
        };
    }

    private static ZatcaClearanceResult ParseClearanceResult(JsonDocument doc)
    {
        var root = doc.RootElement;
        return new ZatcaClearanceResult
        {
            InvoiceUuid          = GetString(root, "invoiceUUID"),
            InvoiceHash          = GetString(root, "invoiceHash"),
            Status               = GetString(root, "status"),
            ClearedInvoiceBase64 = TryGetString(root, "clearedInvoice"),
            QrCode               = TryGetString(root, "qrCode"),
            Warnings             = ParseMessages(root, "warningMessages"),
            Errors               = ParseMessages(root, "errorMessages")
        };
    }

    private static ZatcaReportingResult ParseReportingResult(JsonDocument doc)
    {
        var root = doc.RootElement;
        return new ZatcaReportingResult
        {
            InvoiceUuid = GetString(root, "invoiceUUID"),
            InvoiceHash = GetString(root, "invoiceHash"),
            Status      = GetString(root, "status"),
            Warnings    = ParseMessages(root, "warningMessages"),
            Errors      = ParseMessages(root, "errorMessages")
        };
    }

    private static string GetString(JsonElement el, string key) =>
        el.TryGetProperty(key, out var v) ? v.GetString() ?? "" : "";

    private static string? TryGetString(JsonElement el, string key) =>
        el.TryGetProperty(key, out var v) && v.ValueKind != JsonValueKind.Null ? v.GetString() : null;

    private static IReadOnlyList<string> ParseMessages(JsonElement root, string messagesKey)
    {
        if (!root.TryGetProperty("validationResults", out var vr)) return [];
        if (!vr.TryGetProperty(messagesKey, out var arr)) return [];

        return arr.EnumerateArray()
                  .Select(m => m.TryGetProperty("message", out var msg) ? msg.GetString() ?? "" : "")
                  .Where(m => m.Length > 0)
                  .ToList();
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "...";

    public void Dispose() => _http.Dispose();
}

internal class ComplianceCheckResponse
{
    public string               InvoiceUuid { get; init; } = "";
    public string               InvoiceHash { get; init; } = "";
    public string               Status      { get; init; } = "";
    public IReadOnlyList<string> Warnings   { get; init; } = [];
    public IReadOnlyList<string> Errors     { get; init; } = [];
}
