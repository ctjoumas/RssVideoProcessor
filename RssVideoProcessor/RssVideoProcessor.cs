using Azure.Storage.Blobs;
using global::RssVideoProcessor.Services;
using global::RssVideoProcessor.Util;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Text;
using System.Web;
using System.Xml;

namespace RssVideoProcessor
{
    public class RssVideoProcessor
    {
        private readonly ILogger<RssVideoProcessor> _logger;

        private AzureBlobService _azureBlobService;
        private static readonly HttpClient httpClient = new HttpClient() { DefaultRequestHeaders = { { "User-Agent", "Azure Function" } } };
        private AzureOpenAIService _azureOpenAIService;
        private AzureAiSearchService _azureAiSearchService;

        public RssVideoProcessor(ILogger<RssVideoProcessor> logger, AzureOpenAIService azureOpenAIService)
        {
            _logger = logger;

            _azureBlobService = new AzureBlobService
            {
                ConnectionString = Environment.GetEnvironmentVariable("BlobConnectionString", EnvironmentVariableTarget.Process),
                ContainerName = Environment.GetEnvironmentVariable("ContainerName", EnvironmentVariableTarget.Process)
            };

            _azureOpenAIService = azureOpenAIService;
            _azureAiSearchService = new AzureAiSearchService();
        }
    
        /// <summary>
        /// Main function that processes the videos from the RSS feed.
        /// </summary>
        /// <param name="req"></param>
        /// <returns></returns>
        [Function("RssVideoProcessor")]
        public async Task<IActionResult> Run([HttpTrigger(AuthorizationLevel.Function, "get", "post")] HttpRequest req)
        {
            _logger.LogInformation("C# HTTP trigger function processed a request.");

            await ProcessRssFeedAsync();

            return new OkObjectResult("Videos Processed");
        }

        /// <summary>
        /// This is the callback URL that Video Indexer posts to where we can get the Video ID. We use this in order to avoid having
        /// to continously poll Video Indexer after uploading a video to determine when it has finished processing.
        /// </summary>
        /// <param name="req">POST request from Video Indexer</param>
        /// <param name="log">Logger</param>
        [Function("GetVideoStatus")]
        public async Task ReceiveVideoIndexerStateUpdate([HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
        {
            _logger.LogInformation($"Received Video Indexer status update - Video ID: {req.Query["id"]} \t Processing State: {req.Query["state"]}");

            try
            {
                // If video is processed
                if (req.Query["state"].Equals(ProcessingState.Processed.ToString()))
                {
                    var promptContent = await GetPromptContentAsync(req.Query["id"]);

                    if (promptContent.Sections != null)
                    {
                        JObject sectionsJson = BuildSectionsJsonAsync(promptContent);

                        // check if we are running the unit test or not; if we are running a unit test, the initial
                        // LLM response will return the answer without any scoring or validation so that the unit test
                        // for correctness can be done. If we are not running a unit test, the initial LLM response
                        // will be run through the validation process to ensure the answer is correct and provide a score
                        // reasoning/thought for the answer in the returned JSON
                        string runUnitTest = req.Query["runUnitTest"].ToString();

                        var chatResponse = await _azureOpenAIService.GetChatResponseAsync(sectionsJson.ToString(), runUnitTest);
                    }
                    else
                    {
                        _logger.LogInformation($"No prompt content found for video ID {req.Query["id"]}");
                    }
                }
                else if (req.Query["state"].Equals(ProcessingState.Failed.ToString()))
                {
                    _logger.LogInformation($"\nThe video index failed for video ID {req.Query["id"]}.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing video status update");
            }
        }

        /// <summary>
        /// Uploads video to the Video Indexer when a new video is uploaded to the blob storage.
        /// </summary>
        /// <param name="blobClient"></param>
        /// <param name="name"></param>
        /// <param name="context"></param>
        /// <returns></returns>
        [Function("UploadToVideoIndexer")]
        public async Task UploadToVideoIndexer(
        [BlobTrigger("videos/{name}", Connection = "BlobConnectionString")] BlobClient blobClient,
        string name,
        FunctionContext context)
        {
            _logger.LogInformation($"Start UploadToVideoIndexer for blob\n Name: {name}");

            await UploadToVideoIndexerAsync(name);

            _logger.LogInformation($"End UploadToVideoIndexer for blob\n Name: {name}");
        }

        [Function("ProcessPromptContent")]
        public async Task<IActionResult> ProcessPromptContent([HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
        {
            var id = req.Query["id"].ToString();
            if (string.IsNullOrEmpty(id))
            {
                return new BadRequestObjectResult("The 'id' query parameter is missing or empty.");
            }

            var promptContent = await GetPromptContentAsync(req.Query["id"]);
            //var json = JsonConvert.SerializeObject(promptContent);
            //Console.WriteLine(json);

            if (promptContent.Sections == null)
            {
                return new NoContentResult();
            }
          
            JObject sectionsJson = BuildSectionsJsonAsync(promptContent);

            // check if we are running the unit test or not; if we are running a unit test, the initial
            // LLM response will return the answer without any scoring or validation so that the unit test
            // for correctness can be done. If we are not running a unit test, the initial LLM response
            // will be run through the validation process to ensure the answer is correct and provide a score
            // reasoning/thought for the answer in the returned JSON
            string runUnitTest = req.Query["runUnitTest"].ToString();

            var chatResponse = await _azureOpenAIService.GetChatResponseAsync(sectionsJson.ToString(), runUnitTest);
            //var chatResponse = await _azureOpenAIService.ParallelCallsWithRetryAsync(sectionsJson);

            if (runUnitTest == "1")
            {
                chatResponse = await _azureOpenAIService.RunUnitTestValidationAsync(chatResponse);
            }

            return new OkObjectResult(chatResponse);
        }

        [Function("IndexPromptContent")]
        public async Task<IActionResult> IndexPromptContent([HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
        {
            var docResponse = string.Empty;

            try
            {
                var id = req.Query["id"].ToString();
                if (string.IsNullOrEmpty(id))
                {
                    return new BadRequestObjectResult("The 'id' query parameter is missing or empty.");
                }

                var promptContent = await GetPromptContentAsync(req.Query["id"]);
                //var json = JsonConvert.SerializeObject(promptContent);
                //Console.WriteLine(json);

                if (promptContent.Sections == null)
                {
                    return new NoContentResult();
                }

                JObject sectionsJson = BuildSectionsJsonAsync(promptContent);

                docResponse = await _azureAiSearchService.IndexPromptContent(promptContent.VideoName, id, sectionsJson);
            }
            catch(Exception exc)
            {
                Console.WriteLine(exc);
            }

            return new OkObjectResult(docResponse);
        }

        [Function("GetIndexedPromptContent")]
        public async Task<IActionResult> GetIndexedPromptContent([HttpTrigger(AuthorizationLevel.Function, "get")] HttpRequest req)
        {
            var indexedContent = string.Empty;
            try
            {
                var videoName = req.Query["videoName"].ToString();
                if (string.IsNullOrEmpty(videoName))
                {
                    return new BadRequestObjectResult("The 'videoName' query parameter is missing or empty.");
                }

                indexedContent = await _azureAiSearchService.GetPromptContentAsync(videoName);
            }
            catch (Exception exc)
            {
                Console.WriteLine(exc);
            }

            return new OkObjectResult(indexedContent);
        }

        [Function("SimpleVectorSearch")]
        public async Task<IActionResult> SimpleVectorSearch([HttpTrigger(AuthorizationLevel.Function, "get")] HttpRequest req)
        {
            var indexedContent = string.Empty;

            try
            {
                var text = req.Query["text"].ToString();
                if (string.IsNullOrEmpty(text))
                {
                    return new BadRequestObjectResult("The 'text' query parameter is missing or empty.");
                }

                indexedContent = await _azureAiSearchService.SimpleVectorSearch(text);
            }
            catch (Exception exc)
            {
                Console.WriteLine(exc);
            }

            return new OkObjectResult(indexedContent);
        }

        private async Task<PromptContent> GetPromptContentAsync(string videoId)
        {
            // Build Azure Video Indexer resource provider client that has access token throuhg ARM
            var videoIndexerResourceProviderClient = await VideoIndexerResourceProviderClient.BuildVideoIndexerResourceProviderClient();

            // Get account details
            var account = await videoIndexerResourceProviderClient.GetAccount(_logger);
            var accountLocation = account.Location;
            var accountId = account.Properties.Id;

            // Get account level access token for Azure Video Indexer 
            var accountAccessToken = await videoIndexerResourceProviderClient.GetAccessToken(ArmAccessTokenPermission.Contributor, ArmAccessTokenScope.Account, _logger);

            var promptContent = await videoIndexerResourceProviderClient.GetPromptContentHelper(videoId, accountLocation, accountId, accountAccessToken);

            return promptContent;
        }

        private async Task ProcessRssFeedAsync()
        {
            if (!int.TryParse(Environment.GetEnvironmentVariable("NumberOfVideosToProcess", EnvironmentVariableTarget.Process), out int numberOfVideosToProcess))
            {
                numberOfVideosToProcess = 1; // Default value if the environment variable is missing or invalid
            }

            var requestMessage = new HttpRequestMessage(HttpMethod.Get, "https://denver.granicus.com/ViewPublisherRSS.php?view_id=21&mode=vpodcast");
            var response = await httpClient.SendAsync(requestMessage);

            if (response.Content != null)
            {
                // Parse the RSS feed
                var reader = new XmlTextReader(response.Content.ReadAsStream());

                // ignore all whitespace nodes
                reader.WhitespaceHandling = WhitespaceHandling.None;

                var xml = await response.Content.ReadAsStringAsync();

                XmlDocument document = new XmlDocument();
                document.LoadXml(xml);
                XmlNodeList itemNodes = document.SelectNodes("//item");

                var videoCounter = 0;
                // Process each item
                foreach (XmlNode itemNode in itemNodes)
                {
                    if (videoCounter == numberOfVideosToProcess)
                    {
                        break;
                    }

                    videoCounter += 1;
                    // in this item node, we will have a bunch of differentitems, but we only care about the title and cnn-article:body nodes
                    foreach (XmlNode itemChildNode in itemNode)
                    {
                        if (itemChildNode.Name.ToLower().Equals("enclosure"))
                        {
                            Uri videoUri = new Uri(itemChildNode.Attributes["url"].Value);

                            byte[] videoStream = await httpClient.GetByteArrayAsync(videoUri);

                            var queryParams = HttpUtility.ParseQueryString(videoUri.AbsoluteUri);
                            string clipId = queryParams["clip_id"];
                            string videoName = $"{clipId}.mp4";

                            using MemoryStream ms = new MemoryStream(videoStream, false);
                            await _azureBlobService.UploadFromStreamAsync(ms, videoName);
                        }
                    }
                }
            }
        }

        private async Task UploadToVideoIndexerAsync(string videoName)
        {
            try
            {
                // Build Azure Video Indexer resource provider client that has access token through ARM
                var videoIndexerResourceProviderClient = await VideoIndexerResourceProviderClient.BuildVideoIndexerResourceProviderClient();

                // Get the SAS URL for the video
                var sasUri = _azureBlobService.GetBlobSasUri(videoName);

                // Get account details
                var account = await videoIndexerResourceProviderClient.GetAccount(_logger);
                var accountLocation = account.Location;
                var accountId = account.Properties.Id;

                // Get account level access token for Azure Video Indexer 
                var accountAccessToken = await videoIndexerResourceProviderClient.GetAccessToken(ArmAccessTokenPermission.Contributor, ArmAccessTokenScope.Account, _logger);

                // Upload the video
                await videoIndexerResourceProviderClient.UploadVideo(videoName, sasUri, accountLocation, accountId, accountAccessToken, httpClient, _logger);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An error occurred during the video upload process.");
                throw; // Optionally re-throw the exception if you want to propagate it
            }
        }

        private JObject BuildSectionsJsonAsync(PromptContent promptContent)
        {
            // Initialize a JArray to hold all the sections
            JArray sectionsArray = new JArray();

            // Loop through each section in the PromptContent object
            foreach (var section in promptContent.Sections)
            {
                // Create a JSON object for each section
                JObject sectionObject = new JObject
                {
                    ["start"] = section.Start,    // Start time of the section
                    ["end"] = section.End,        // End time of the section
                    ["content"] = section.Content // Content details for the section
                };

                // Add the section to the sections array
                sectionsArray.Add(sectionObject);
            }

            // Create the final JSON object to return
            JObject videoJson = new JObject
            {
                ["sections"] = sectionsArray
            };

            return videoJson;
        }
    }

    public enum ProcessingState
    {
        Uploaded,
        Processing,
        Processed,
        Failed
    }
}