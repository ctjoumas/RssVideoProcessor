using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Text;

public class AzureOpenAIService
{
    private const string SystemPrompt = @"
        You are an AI assistant that helps generate JSON documents by reading the input JSON list and creating a new one with metadata details generated using the input.
        You are provided a list of JSONs which contains chunks from a video file. Each chunk is from within a time window in the original video file and contains the content from
        that chunk as captured by the Azure Video Indexer service. For the video chunks provided in the user message, modify each chunk by adding a summary field and an actionableInsights 
        field. The summary field should contain the summary of the content field and the actionableInsights field should contain any action insights from the content field. 
        The output JSON list should have the same structure as the input JSON list but the new JSONs should only contain the new metadata fields as specified above along with
        the chunk timestamps from the original prompt chunks.";

    private readonly string _azureOpenAIUrl;
    private readonly string _apiKey;
    private readonly IHttpClientFactory _httpClientFactory;

    public AzureOpenAIService(IHttpClientFactory httpClientFactory)
    {
        _apiKey = Environment.GetEnvironmentVariable("AzureOpenAIKey", EnvironmentVariableTarget.Process);
        var endPoint = Environment.GetEnvironmentVariable("AzureOpenAIEndpoint", EnvironmentVariableTarget.Process);
        var modelName = Environment.GetEnvironmentVariable("ModelName", EnvironmentVariableTarget.Process);
        var apiVersion = Environment.GetEnvironmentVariable("ApiVersion", EnvironmentVariableTarget.Process);
        _httpClientFactory = httpClientFactory;
        _azureOpenAIUrl = $"{endPoint}{modelName}/chat/completions?api-version={apiVersion}";
    }

    public async Task<string> GetChatResponseAsync(string prompt)
    {
        using HttpClient client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Add("api-key", _apiKey);

        // Define the request payload for a chat completion
        var requestPayload = new
        {
            messages = new[]
            {
                    new { role = "system", content = $"{SystemPrompt}\n\n Only use the provided context, do not reply otherwise. Context: {prompt}" },
                    new { role = "user", content = "Using the provided context, please scan the content to determine if any key decisions were made." }
                },
            max_tokens = 4096,  // Define the maximum number of tokens
            temperature = 0.7,  // Optional, controls randomness of the response,
            response_format = new
            {
                type = "json_schema",
                json_schema = new
                {
                    name = "KeyInsightsResponse",
                    strict = true,
                    schema = new
                    {
                        type = "object",
                        properties = new
                        {
                            start = new { type = "string" },
                            end = new { type = "string" },
                            key_decision = new
                            {
                                type = "array",
                                items = new
                                {
                                    type = "object",
                                    properties = new
                                    {
                                        decision = new { type = "string" },
                                        confidence = new { type = "number" }
                                    },
                                    required = new[] { "decision", "confidence" },
                                    additionalProperties = false
                                }
                            }
                        },
                        required = new[] { "start", "end", "key_decision" },
                        additionalProperties = false
                    }
                }
            }
        };

        // Serialize the payload to JSON
        var jsonPayload = JsonConvert.SerializeObject(requestPayload);
        var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

        // Send POST request
        var response = await client.PostAsync(_azureOpenAIUrl, content);

        // Get the response content
        var responseContent = await response.Content.ReadAsStringAsync();

        // Parse the JSON response
        JObject jsonResponse = JObject.Parse(responseContent);

        // Extract the content from the message inside choices[0]
        var messageContent = jsonResponse["choices"]?[0]?["message"]?["content"]?.ToString();

        return messageContent;
    }
}