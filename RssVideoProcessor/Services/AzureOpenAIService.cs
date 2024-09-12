using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RssVideoProcessor.Schemas;
using System.Text;

public class AzureOpenAIService
{
    private const string SystemPrompt = @"
        You are an AI assistant that analyzes insights from a video and extracts key decisions that were made in the meetings from the speakers. 
        You will be given structured JSON in the following format:
        ""sections"": [
            {
              ""start"": ""0:00:00"",
              ""end"": ""0:00:28.12"",
              ""content"": ""[Video title] testName\n[Tags] Beginning\n[Visual labels] logo, font, colorfulness, tree, building, outdoor, sky, cloud, indoor, furniture, court\n[OCR] 08.26.24, DENVER, THE MILE HIGH CITY, CITY COUNCIL, LEGISLATIVE SESSION, NOW, DENVER CITY COUNCIL, WEEKLY LEGISLATIVE SESSION WITH ALL COUNCIL MEMBERS\n[Transcript] Welcome to your Denver City Council.\nPlease stand by.
               Full coverage of your Denver City Council begins now.\nGood afternoon, everyone.""
            },
            {
              ""start"": ""0:00:28.12"",
              ""end"": ""0:03:04.92"",
              ""content"": ""[Video title] testName\n[Tags] Beginning\n[Detected objects] chair, cup, laptop\n[Visual labels] indoor, furniture, human face, laptop, computer, person, 
                clothing, chair, man, flag, smile, woman\n[OCR] 08.26.24, DENVER CITY COUNCIL, WEEKLY LEGISLATIVE SESSION WITH ALL COUNCIL MEMBERS, DELL, DeLL, GILMORE, DEL\n[Transcript] Thank you for joining us.\nTonight's meeting is being interpreted into Spanish.\nSam, would you please introduce yourself and let our viewers know how to enable translation on their devices?\nYes, of course.\nThank you for having us today.\nGood afternoon.\nMy name is Sam Guzman with the CLC, and along with my colleague Alejandro, we will be interpreting today's meeting into Spanish.\nI'm going to give the instructions in Spanish on how to access interpretation.\nBuenastaris Atos MI nom de Samuel Guzman con la SE El SE Y contamente comico le Alejandro esta remos interpretando la reignon de oy El espanol sis Nos a companion oya travez zoom Vito almente por favor busquin supantaya unicono de Globo querice interpretacion O prima seboton EDI selecione a la opcion Perez cuchar en espanol muchas gracias and thank you very much.\nThank you, Sam.\nWelcome to the Denver City Council meeting of Monday, August 26th, 2024.\nCouncil members, please rise as you are able and join Council Member Gilmore in the Pledge of Allegiance.\nCouncil members, please join Council Member Gilmore as they lead us in the Denver City Council Land Acknowledgement.\nThank you, Council President.\nThe Denver City Council honors and acknowledges that the land on which we reside is the traditional territory of the Ute Cheyenne and Arapahoe peoples.\nWe also recognize the 48 contemporary tribal nations that are historically tied to the lands that make up the State of Colorado.\nWe honor Elders past, present, and future, and those who have stewarded this land throughout generations.\nWe also recognize that government, academic, and cultural institutions were founded upon and continue to enact exclusions and erasures of Indigenous peoples.\nMay this acknowledgement demonstrate a commitment to working to dismantle ongoing legacies of oppression and inequalities and recognize the current and future contributions of Indigenous communities in Denver.\nThank you, Councilwoman.\nMadam Secretary, Roll.""
            }
        ]

        Each node of the JSON is a section extracted from the video analysis. If a key decision is made during the meeting, you will return the following list of JSON items for each key decision:

        {
        ""extracted_decision"": [
            {
            ""start"": ""01:24:22"",
            ""end"":""01:26:11"",
            ""key_decision"": ""John mentioned that the decision was made to extend the school day by 15 minutes each day in order to make up for the number 
                of snow days that took place during the school year""
            },
            {
            ""start"": ""01:41:22"",
            ""end"":""01:42:11"",
            ""key_decision"": ""Judy said that the decision was made to add an extra day of PE to each week of school""
            }]
            }";

    private readonly string _azureOpenAIUrl;
    private readonly string _apiKey;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ISchemaLoader _schemaLoader;
    public AzureOpenAIService(IHttpClientFactory httpClientFactory)
    {
        _apiKey = Environment.GetEnvironmentVariable("AzureOpenAIKey", EnvironmentVariableTarget.Process);
        var endPoint = Environment.GetEnvironmentVariable("AzureOpenAIEndpoint", EnvironmentVariableTarget.Process);
        var modelName = Environment.GetEnvironmentVariable("ModelName", EnvironmentVariableTarget.Process);
        var apiVersion = Environment.GetEnvironmentVariable("ApiVersion", EnvironmentVariableTarget.Process);
        _httpClientFactory = httpClientFactory;
        _azureOpenAIUrl = $"{endPoint}{modelName}/chat/completions?api-version={apiVersion}";
        _schemaLoader = new SchemaLoader(@".\Schemas");
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
                    new { role = "system", content = $"{SystemPrompt}\n\n Only use the provided context, do not reply otherwise. Only return properly structured JSON as the response. Context: {prompt}" },
                    new { role = "user", content = "Using the provided context, please scan the content to determine if any key decisions were made." }
                },
            max_tokens = 4096,  // Define the maximum number of tokens
            temperature = 0.7,  // Optional, controls randomness of the response
            response_format = new { type="json_object" }
            //response_format = new 
            //{ 
            //    type = "json_object", 
            //    json_schema = _schemaLoader.LoadSchema("Insights.json")
            //},
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

    public async Task<string> GetChunkedChatResponseAsync(string prompt, int chunkSize = 3000)
    {
        // Split the prompt into chunks of manageable size (below the token limit of 4096)
        List<string> promptChunks = SplitIntoChunks(prompt, chunkSize);

        // Create a list of tasks to handle the API calls concurrently
        List<Task<string>> tasks = new List<Task<string>>();

        foreach (var chunk in promptChunks)
        {
            tasks.Add(GetChatResponseAsync(chunk));
        }

        string[] responses = await Task.WhenAll(tasks);

        // Initialize a list to aggregate the decisions
        JArray aggregatedDecisions = new JArray();

        // Iterate through each response, parse, and extract valid decisions
        foreach (var response in responses)
        {
            if (!string.IsNullOrWhiteSpace(response) && HasValidDecisions(response))
            {
                JObject parsedResponse = JObject.Parse(response);
                JArray extractedDecision = (JArray)parsedResponse["extracted_decision"];

                if (extractedDecision != null && extractedDecision.Count > 0)
                {
                    // Add the extracted decisions from this chunk to the aggregated list
                    aggregatedDecisions.Merge(extractedDecision);
                }
            }
        }

        // Create the final structured response with "extracted_decision" at the top
        JObject finalResponse = new JObject
        {
            ["extracted_decision"] = aggregatedDecisions
        };

        return finalResponse.ToString();
    }

    // Helper method to check if the response contains valid decisions
    private bool HasValidDecisions(string jsonResponse)
    {
        if (string.IsNullOrWhiteSpace(jsonResponse))
        {
            return false;
        }

        JObject parsedResponse = JObject.Parse(jsonResponse);
        JArray extractedDecision = (JArray)parsedResponse["extracted_decision"];

        return extractedDecision != null && extractedDecision.Count > 0;
    }

    private List<string> SplitIntoChunks(string text, int chunkSize)
    {
        List<string> chunks = new List<string>();

        // Start splitting the text into chunks of the specified size
        for (int i = 0; i < text.Length; i += chunkSize)
        {
            // Ensure we don't exceed the length of the text
            int length = Math.Min(chunkSize, text.Length - i);

            chunks.Add(text.Substring(i, length));
        }

        return chunks;
    }
}