using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
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
            string personId = req.Query["person-id"];
            if (string.IsNullOrEmpty(personId))
            {
                return new BadRequestObjectResult("Missing person-id in query parameters.");
            }
            
            var requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            dynamic data = JsonConvert.DeserializeObject(requestBody);
            string fileUrl = data?.fileUrl;
            if (string.IsNullOrEmpty(fileUrl))
            {
                return new BadRequestObjectResult("Missing fileUrl in request body.");
            }

            using var httpClient = new HttpClient();
            
            var response = await httpClient.GetAsync(fileUrl);
            if (!response.IsSuccessStatusCode)
            {
                log.LogError($"Failed to download file. HTTP status code: {response.StatusCode}");
                return new StatusCodeResult((int)response.StatusCode);
            }

            var fileData = await response.Content.ReadAsByteArrayAsync();
            log.LogInformation($"File downloaded successfully. Size: {fileData.Length} bytes");

            httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", _settings.BearerToken);

            using (var content = new MultipartFormDataContent())
            {
                var fileContent = new ByteArrayContent(fileData);
                fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                var filename = Path.GetFileName(new Uri(fileUrl).LocalPath);
                content.Add(fileContent, "document", filename);

                var postResponse = await httpClient.PostAsync(
                    $"https://app.loxo.co/api/{_settings.AgencySlug}/people/{personId}/documents", content);

                if (!postResponse.IsSuccessStatusCode)
                {
                    log.LogError($"Failed to post file. HTTP status code: {postResponse.StatusCode}");
                    return new StatusCodeResult((int)postResponse.StatusCode);
                }
            }

            log.LogInformation("File posted successfully.");
            return new OkResult();
        }
    }
}