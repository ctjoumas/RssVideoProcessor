namespace RssVideoProcessor
{
    using Azure;
    using global::RssVideoProcessor.Services;
    using global::RssVideoProcessor.Util;
    using Microsoft.AspNetCore.Http;
    using Microsoft.AspNetCore.Mvc;
    using Microsoft.Azure.Functions.Worker;
    using Microsoft.Extensions.Logging;
    using Microsoft.Identity.Client;
    using System;
    using System.Net;
    using System.Security.Policy;
    using System.Xml;

    public class RssVideoProcessor
    {
        private readonly ILogger<RssVideoProcessor> _logger;

        private AzureBlobService _azureBlobService;

        public RssVideoProcessor(ILogger<RssVideoProcessor> logger)
        {
            _logger = logger;

            _azureBlobService = new AzureBlobService
            {
                ConnectionString = Environment.GetEnvironmentVariable("BlobConnectionString", EnvironmentVariableTarget.Process),
                ContainerName = Environment.GetEnvironmentVariable("ContainerName", EnvironmentVariableTarget.Process)
            };
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

            // If video is processed
            if (req.Query["state"].Equals(ProcessingState.Processed.ToString()))
            {
                // Build Azure Video Indexer resource provider client that has access token throuhg ARM
                var videoIndexerResourceProviderClient = await VideoIndexerResourceProviderClient.BuildVideoIndexerResourceProviderClient();

                // Get account details
                var account = await videoIndexerResourceProviderClient.GetAccount(_logger);
                var accountLocation = account.Location;
                var accountId = account.Properties.Id;

                // Get account level access token for Azure Video Indexer 
                var accountAccessToken = await videoIndexerResourceProviderClient.GetAccessToken(ArmAccessTokenPermission.Contributor, ArmAccessTokenScope.Account, _logger);

                PromptContent promptContent = await videoIndexerResourceProviderClient.GetPromptContentHelper(req.Query["id"], accountLocation, accountId, accountAccessToken);

                //await videoIndexerResourceProviderClient.GetVideoCaptions(req.Query["id"], _logger);
                //await GetVideoCaptions(req.Query["id"]);

                // perform AI
            }
            else if (req.Query["state"].Equals(ProcessingState.Failed.ToString()))
            {
                _logger.LogInformation($"\nThe video index failed for video ID {req.Query["id"]}.");
            }
        }

        private async Task ProcessRssFeedAsync()
        {
            HttpClient client = new HttpClient();

            var requestMessage = new HttpRequestMessage(HttpMethod.Get, "https://denver.granicus.com/ViewPublisherRSS.php?view_id=21&mode=vpodcast");
            var response = await client.SendAsync(requestMessage);

            if (response.Content != null)
            {
                // Parse the RSS feed
                XmlTextReader reader = new XmlTextReader(response.Content.ReadAsStream());

                // ignore all whitespace nodes
                reader.WhitespaceHandling = WhitespaceHandling.None;

                string xml = await response.Content.ReadAsStringAsync();

                XmlDocument document = new XmlDocument();
                document.LoadXml(xml);
                XmlNodeList itemNodes = document.SelectNodes("//item");

                // Process each item
                foreach (XmlNode itemNode in itemNodes)
                {
                    // in this item node, we will have a bunch of differentitems, but we only care about the title and cnn-article:body nodes
                    foreach (XmlNode itemChildNode in itemNode)
                    {
                        if (itemChildNode.Name.ToLower().Equals("enclosure"))
                        {
                            Uri sasUri;

                            Uri videoUri = new Uri(itemChildNode.Attributes["url"].Value);

                            // upload the file to blob storage so we can then use that SAS URL to upload to Video Indexer
                            //using (client = new HttpClient())
                            //{
                                client.DefaultRequestHeaders.Add("User-Agent", "C# console program");

                                byte[] videoStream = await client.GetByteArrayAsync(videoUri);

                                using (MemoryStream ms = new MemoryStream(videoStream, false))
                                {
                                    // TODO: GET VIDEO NAME FROM RSS FEED
                                    await _azureBlobService.UploadFromStreamAsync(ms, "testName.mp4");
                                }

                                // Get the SAS URL for the video
                                sasUri = _azureBlobService.GetBlobSasUri($"testName.mp4");
                            //}

                            string videoName = "testName.mp4";

                            // upload the video
                            // Build Azure Video Indexer resource provider client that has access token throuhg ARM
                            var videoIndexerResourceProviderClient = await VideoIndexerResourceProviderClient.BuildVideoIndexerResourceProviderClient();

                            // Get account details
                            var account = await videoIndexerResourceProviderClient.GetAccount(_logger);
                            var accountLocation = account.Location;
                            var accountId = account.Properties.Id;

                            // Get account level access token for Azure Video Indexer 
                            var accountAccessToken = await videoIndexerResourceProviderClient.GetAccessToken(ArmAccessTokenPermission.Contributor, ArmAccessTokenScope.Account, _logger);

                            // Upload the video
                            await videoIndexerResourceProviderClient.UploadVideo(videoName, sasUri, accountLocation, accountId, accountAccessToken, client, _logger);
                        }
                    }
                }
            }            
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
