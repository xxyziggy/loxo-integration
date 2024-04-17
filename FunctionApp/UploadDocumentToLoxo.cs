using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using System.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LoxoIntegration
{
    public class UploadDocumentToLoxo
    {
        private readonly LoxoSettings _settings;

        public UploadDocumentToLoxo(IOptions<LoxoSettings> options)
        {
            _settings = options.Value;
        }

        [FunctionName("upload-document-to-loxo")]
        public async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Function, "post", Route = null)]
            HttpRequest req,
            ILogger log)
        {
            var data = await ParseRequestBody(req);
            var validationResult = ValidateData(data);
            if (validationResult != null)
            {
                return validationResult;
            }

            using var httpClient = CreateHttpClient(data.slug);
            var personId = await GetPersonId(httpClient, data.slug, data.email, log);
            if (personId == null)
            {
                return new StatusCodeResult(StatusCodes.Status404NotFound);
            }

            var fileData = await DownloadFile(httpClient, data.fileUrl, log);
            if (fileData == null)
            {
                return new StatusCodeResult(StatusCodes.Status404NotFound);
            }

            var postResult = await PostFile(httpClient, data.slug, personId.Value, fileData, log);
            if (!postResult)
            {
                return new StatusCodeResult(StatusCodes.Status500InternalServerError);
            }

            log.LogInformation("File posted successfully.");
            return new OkResult();
        }

        private async Task<(string fileUrl, string slug, string email)> ParseRequestBody(HttpRequest req)
        {
            var requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            dynamic data = JsonConvert.DeserializeObject(requestBody);
            return (data?.fileUrl, data?.data?.slug, data?.data?.email);
        }

        private static IActionResult ValidateData((string fileUrl, string slug, string email) data)
        {
            if (string.IsNullOrEmpty(data.fileUrl))
            {
                return new BadRequestObjectResult("Missing fileUrl in request body.");
            }

            if (string.IsNullOrEmpty(data.email))
            {
                return new BadRequestObjectResult("Missing email in request body.");
            }

            if (string.IsNullOrEmpty(data.slug))
            {
                return new BadRequestObjectResult("Missing slug in request body.");
            }

            return null;
        }

        private HttpClient CreateHttpClient(string slug)
        {
            var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", _settings.Tokens[slug]);
            return httpClient;
        }

        private async Task<int?> GetPersonId(HttpClient httpClient, string slug, string email, ILogger log)
        {
            var personResponse = await httpClient.GetAsync(
                $"https://app.loxo.co/api/{slug}/people?query=emails:\"{HttpUtility.UrlEncode(email.Trim())}\"");
            if (!personResponse.IsSuccessStatusCode)
            {
                log.LogError($"Failed to get person with email {email}. HTTP status code: {personResponse.StatusCode}");
                return null;
            }

            dynamic person = JsonConvert.DeserializeObject(await personResponse.Content.ReadAsStringAsync());
            return (person?.people as JArray)?.FirstOrDefault()?["id"]?.Value<int>();
        }

        private async Task<byte[]> DownloadFile(HttpClient httpClient, string fileUrl, ILogger log)
        {
            var response = await httpClient.GetAsync(fileUrl);
            if (!response.IsSuccessStatusCode)
            {
                log.LogError($"Failed to download file {fileUrl}. HTTP status code: {response.StatusCode}");
                return null;
            }

            var fileData = await response.Content.ReadAsByteArrayAsync();
            log.LogInformation($"File downloaded successfully. Size: {fileData.Length} bytes");
            return fileData;
        }

        private async Task<bool> PostFile(HttpClient httpClient, string slug, int personId, byte[] fileData,
            ILogger log)
        {
            var fileContent = new ByteArrayContent(fileData);
            var fileName = $"Form_{DateTime.Now:yyyy_MM_dd_HH_mm_ss}.pdf";
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

            using var content = new MultipartFormDataContent();
            content.Add(fileContent, "document", fileName);
            
            var postResponse = await httpClient.PostAsync(
                $"https://app.loxo.co/api/{slug}/people/{personId}/documents", content);
            if (!postResponse.IsSuccessStatusCode)
            {
                log.LogError($"Failed to post file. HTTP status code: {postResponse.StatusCode}");
                return false;
            }

            return true;
        }
    }
}
