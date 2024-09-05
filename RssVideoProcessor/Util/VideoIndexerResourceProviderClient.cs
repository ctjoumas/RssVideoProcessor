namespace RssVideoProcessor.Util
{
    using Azure.Core;
    using Azure.Identity;
    using Microsoft.Extensions.Logging;
    using Microsoft.Identity.Client;
    using Newtonsoft.Json.Linq;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net.Http;
    using System.Net.Http.Headers;
    using System.Text;
    using System.Text.Json.Serialization;
    using System.Threading.Tasks;
    using System.Web;

    internal class VideoIndexerResourceProviderClient
    {
        private const string ApiUrl = "https://api.videoindexer.ai";
        private const string AzureResourceManager = "https://management.azure.com";
        private const string ApiVersion = "2022-08-01";
        private string ResourceGroup = Environment.GetEnvironmentVariable("ResourceGroup");
        private string SubscriptionId = Environment.GetEnvironmentVariable("SubscriptionId");
        private string AccountName = Environment.GetEnvironmentVariable("AccountName");
        private static string FunctionCallbackUrl = Environment.GetEnvironmentVariable("FunctionCallbackUrl");
        private readonly string armAccessToken;

        /// <summary>
        /// Builds the Video Indexer Resource Provider Client with the proper token for authorization.
        /// </summary>
        /// <returns></returns>
        async public static Task<VideoIndexerResourceProviderClient> BuildVideoIndexerResourceProviderClient()
        {
            var tokenRequestContext = new TokenRequestContext(new[] { $"{AzureResourceManager}/.default" });
            var tokenRequestResult = await new DefaultAzureCredential(new DefaultAzureCredentialOptions { ExcludeEnvironmentCredential = true }).GetTokenAsync(tokenRequestContext);

            return new VideoIndexerResourceProviderClient(tokenRequestResult.Token);
        }

        public VideoIndexerResourceProviderClient(string armAaccessToken)
        {
            this.armAccessToken = armAaccessToken;
        }

        /// <summary>
        /// Generates an access token. Calls the generateAccessToken API  (https://github.com/Azure/azure-rest-api-specs/blob/main/specification/vi/resource-manager/Microsoft.VideoIndexer/stable/2022-08-01/vi.json#:~:text=%22/subscriptions/%7BsubscriptionId%7D/resourceGroups/%7BresourceGroupName%7D/providers/Microsoft.VideoIndexer/accounts/%7BaccountName%7D/generateAccessToken%22%3A%20%7B)
        /// </summary>
        /// <param name="permission"> The permission for the access token</param>
        /// <param name="scope"> The scope of the access token </param>
        /// <param name="videoId"> if the scope is video, this is the video Id </param>
        /// <param name="projectId"> If the scope is project, this is the project Id </param>
        /// <returns> The access token, otherwise throws an exception</returns>
        public async Task<string> GetAccessToken(ArmAccessTokenPermission permission, ArmAccessTokenScope scope, ILogger log)
        {
            var accessTokenRequest = new AccessTokenRequest
            {
                PermissionType = permission,
                Scope = scope
            };

            log.LogInformation($"\nGetting access token: {System.Text.Json.JsonSerializer.Serialize(accessTokenRequest)}");

            // Set the generateAccessToken (from video indexer) http request content
            try
            {
                var jsonRequestBody = System.Text.Json.JsonSerializer.Serialize(accessTokenRequest);
                var httpContent = new StringContent(jsonRequestBody, Encoding.UTF8, "application/json");

                // Set request uri
                var requestUri = $"{AzureResourceManager}/subscriptions/{SubscriptionId}/resourcegroups/{ResourceGroup}/providers/Microsoft.VideoIndexer/accounts/{AccountName}/generateAccessToken?api-version={ApiVersion}";
                var client = new HttpClient(new HttpClientHandler());
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", armAccessToken);

                var result = await client.PostAsync(requestUri, httpContent);

                VerifyStatus(result, System.Net.HttpStatusCode.OK);
                var jsonResponseBody = await result.Content.ReadAsStringAsync();

                log.LogInformation($"Got access token: {scope}, {permission}");

                return System.Text.Json.JsonSerializer.Deserialize<GenerateAccessTokenResponse>(jsonResponseBody).AccessToken;
            }
            catch (Exception ex)
            {
                log.LogInformation(ex.ToString());
                throw;
            }
        }

        /// <summary>
        /// Gets an account. Calls the getAccount API (https://github.com/Azure/azure-rest-api-specs/blob/main/specification/vi/resource-manager/Microsoft.VideoIndexer/stable/2022-08-01/vi.json#:~:text=%22/subscriptions/%7BsubscriptionId%7D/resourceGroups/%7BresourceGroupName%7D/providers/Microsoft.VideoIndexer/accounts/%7BaccountName%7D%22%3A%20%7B)
        /// </summary>
        /// <returns>The Account, otherwise throws an exception</returns>
        public async Task<Account> GetAccount(ILogger log)
        {
            log.LogInformation($"Getting account {AccountName}.");

            Account account;

            try
            {
                // Set request uri
                var requestUri = $"{AzureResourceManager}/subscriptions/{SubscriptionId}/resourcegroups/{ResourceGroup}/providers/Microsoft.VideoIndexer/accounts/{AccountName}?api-version={ApiVersion}";
                log.LogInformation($"Requesting Video Indexer Account Name: {requestUri}");
                var client = new HttpClient(new HttpClientHandler());
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", armAccessToken);

                var result = await client.GetAsync(requestUri);

                VerifyStatus(result, System.Net.HttpStatusCode.OK);

                var jsonResponseBody = await result.Content.ReadAsStringAsync();
                account = System.Text.Json.JsonSerializer.Deserialize<Account>(jsonResponseBody);

                VerifyValidAccount(account, log);

                log.LogInformation($"The account ID is {account.Properties.Id}");
                log.LogInformation($"The account location is {account.Location}");

                return account;
            }
            catch (Exception ex)
            {
                log.LogInformation(ex.ToString());
                throw;
            }
        }

        /// <summary>
        /// Uploads the video from a station to the Video Indexer.
        /// </summary>
        /// <param name="videoName"></param>
        /// <param name="videoUri"></param>
        /// <param name="accountLocation"></param>
        /// <param name="accountId"></param>
        /// <param name="accountAccessToken"></param>
        /// <param name="client"></param>
        /// <param name="log"></param>
        /// <returns></returns>
        public async Task UploadVideo(string videoName, Uri videoUri, string accountLocation, string accountId, string accountAccessToken, HttpClient client, ILogger logger)
        {
            logger.LogInformation($"Video is starting to upload with video name: {videoName}, videoUri: {videoUri}");

            var content = new MultipartFormDataContent();

            //string functionCallbackUrl = await GetFunctionCallbackUrl();

            try
            {
                var queryParams = CreateQueryString(
                    new Dictionary<string, string>()
                    {
                        {"accessToken", accountAccessToken},
                        {"name", videoName},
                        {"privacy", "Private"},
                        {"videoUrl", videoUri.ToString()},
                        {"callbackUrl", FunctionCallbackUrl },
                    });

                logger.LogInformation($"API Call: {ApiUrl}/{accountLocation}/Accounts/{accountId}/Videos?{queryParams}");

                var uploadRequestResult = await client.PostAsync($"{ApiUrl}/{accountLocation}/Accounts/{accountId}/Videos?{queryParams}", content);

                VerifyStatus(uploadRequestResult, System.Net.HttpStatusCode.OK);

                var uploadResult = await uploadRequestResult.Content.ReadAsStringAsync();

                // Get the video ID from the upload result
                var videoId = System.Text.Json.JsonSerializer.Deserialize<Video>(uploadResult).Id;
                logger.LogInformation($"\nVideo ID {videoId} was uploaded successfully");
            }
            catch (Exception ex)
            {
                logger.LogInformation(ex.ToString());
                throw;
            }
        }

        /// <summary>
        /// This helper function will first attempt to get the PromptContent from a video. If it doesn't
        /// exist, it will make a POST to the PromptContent API for that video and then check the PromptContent
        /// API until the content is available. TODO: Test with larger videos with more content sections to ensure
        /// it makes sense to check the creation in a loop - I'm assuming it only takes 10 seconds or less for
        /// any size video and there doens't appear to be a way to have AVI call back to an endpoint to notify
        /// when the prompt content has been created.
        /// </summary>
        /// <param name="videoId"></param>
        /// <returns></returns>
        public async Task<PromptContent> GetPromptContentHelper(string videoId, string accountLocation, string accountId, string accountAccessToken)
        {
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = false,
            };
            HttpClient client = new HttpClient(handler);

            var queryParams = CreateQueryString(
                new Dictionary<string, string>()
                {
                    {"accessToken", accountAccessToken},
                });

            var promptContentResult = await client.GetAsync($"{ApiUrl}/{accountLocation}/Accounts/{accountId}/Videos/{videoId}/PromptContent?{queryParams}");

            // if we get a 404, the prompt content doesn't exist for this video, so we need to create it
            if (promptContentResult.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                Console.WriteLine("Prompt does not exist for this video, creating...");
                // Call endpoint to create prompt content for this video
                var createPromptContentResult = await client.PostAsync($"{ApiUrl}/{accountLocation}/Accounts/{accountId}/Videos/{videoId}/PromptContent?{queryParams}", null);

                bool promptContentCreated = false;

                // it might take a little time for the prompt to get created, so we'll wait 3 seconds and get the prompt again
                // to see if it's there
                while (!promptContentCreated)
                {
                    promptContentResult = await client.GetAsync($"{ApiUrl}/{accountLocation}/Accounts/{accountId}/Videos/{videoId}/PromptContent?{queryParams}");

                    if (promptContentResult.StatusCode == System.Net.HttpStatusCode.OK)
                    {
                        promptContentCreated = true;
                    }
                    else
                    {
                        Thread.Sleep(3000);
                    }
                }
            }

            string strPromptContentResult = await promptContentResult.Content.ReadAsStringAsync();

            JObject promptContentJsonObject = JObject.Parse(strPromptContentResult);

            PromptContent promptContent = System.Text.Json.JsonSerializer.Deserialize<PromptContent>(promptContentJsonObject.ToString());

            return promptContent;
        }

        /// <summary>
        /// Gets the captions for the given video in the provided format (VTT, TTML, SRT, TXT, or CSV). See
        /// https://api-portal.videoindexer.ai/api-details#api=Operations&operation=Get-Video-Captions
        /// </summary>
        /// <param name="videoId"></param>
        /// <param name="log"></param>
        /// <returns></returns>
        public async Task GetVideoCaptions(string videoId, ILogger logger)
        {
            // we don't have the video name and will need to get it from Video Indexer, so let's do that first
            // Build Azure Video Indexer resource provider client that has access token throuhg ARM
            //var videoIndexerResourceProviderClient = await VideoIndexerResourceProviderClient.BuildVideoIndexerResourceProviderClient();

            // Get account details
            var account = await GetAccount(logger);
            var accountLocation = account.Location;
            var accountId = account.Properties.Id;

            // Get account level access token for Azure Video Indexer 
            var accountAccessToken = await GetAccessToken(ArmAccessTokenPermission.Contributor, ArmAccessTokenScope.Account, logger);

            string queryParams = CreateQueryString(
                new Dictionary<string, string>()
                {
                    { "accessToken", accountAccessToken },
                    // Allowed values: Vtt / Ttml / Srt / Txt / Csv
                    { "format", "Vtt" },
                    { "language", "English" },
                });

            // Create the http client in order to get the JSON Insights of the video
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = false
            };

            var client = new HttpClient(handler);

            var videoCaptionsRequestResult = await client.GetAsync($"{ApiUrl}/{accountLocation}/Accounts/{accountId}/Videos/{videoId}/Captions?{queryParams}");

            VerifyStatus(videoCaptionsRequestResult, System.Net.HttpStatusCode.OK);

            var videoCaptionsResult = await videoCaptionsRequestResult.Content.ReadAsStringAsync();

            logger.LogInformation($"Captions of the video for video ID {videoId}: \n{videoCaptionsResult}");
        }

        static string CreateQueryString(IDictionary<string, string> parameters)
        {
            var queryParameters = HttpUtility.ParseQueryString(string.Empty);
            foreach (var parameter in parameters)
            {
                queryParameters[parameter.Key] = parameter.Value;
            }

            return queryParameters.ToString();
        }

        private void VerifyValidAccount(Account account, ILogger log)
        {
            if (string.IsNullOrWhiteSpace(account.Location) || account.Properties == null || string.IsNullOrWhiteSpace(account.Properties.Id))
            {
                log.LogInformation($"{nameof(AccountName)} {AccountName} not found. Check {nameof(SubscriptionId)}, {nameof(ResourceGroup)}, {nameof(AccountName)} ar valid.");

                throw new Exception($"Account {AccountName} not found.");
            }
        }

        public static void VerifyStatus(HttpResponseMessage response, System.Net.HttpStatusCode excpectedStatusCode)
        {
            if (response.StatusCode != excpectedStatusCode)
            {
                throw new Exception(response.ToString());
            }
        }
    }

    public class AccessTokenRequest
    {
        [JsonPropertyName("permissionType")]
        public ArmAccessTokenPermission PermissionType { get; set; }

        [JsonPropertyName("scope")]
        public ArmAccessTokenScope Scope { get; set; }

        /*[JsonPropertyName("projectId")]
        public string ProjectId { get; set; }

        [JsonPropertyName("videoId")]
        public string VideoId { get; set; }*/
    }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum ArmAccessTokenPermission
    {
        Reader,
        Contributor,
        MyAccessAdministrator,
        Owner,
    }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum ArmAccessTokenScope
    {
        Account,
        Project,
        Video
    }

    public class AccountProperties
    {
        [JsonPropertyName("accountId")]
        public string Id { get; set; }
    }

    public class Account
    {
        [JsonPropertyName("properties")]
        public AccountProperties Properties { get; set; }

        [JsonPropertyName("location")]
        public string Location { get; set; }
    }

    public class GenerateAccessTokenResponse
    {
        [JsonPropertyName("accessToken")]
        public string AccessToken { get; set; }
    }

    public class Video
    {
        [JsonPropertyName("id")]
        public string Id { get; set; }

        [JsonPropertyName("state")]
        public string State { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }
    }

    /// <summary>
    /// Represents the PromptContent API response payload, which will have the name of the vide
    /// followed by a list of sections.
    /// </summary>
    public class PromptContent
    {
        [JsonPropertyName("name")]
        public string VideoName { get; set; }

        [JsonPropertyName("sections")]
        public PromptContentSection[] Sections { get; set; }
    }

    /// <summary>
    /// Represents a section returned from the PromptContentAPI response payload.
    /// </summary>
    public class PromptContentSection
    {
        //[JsonPropertyName("id")]
        //public int Id { get; set; }

        [JsonPropertyName("start")]
        public string Start { get; set; }

        [JsonPropertyName("end")]
        public string End { get; set; }

        [JsonPropertyName("content")]
        public string Content { get; set; }
    }
}