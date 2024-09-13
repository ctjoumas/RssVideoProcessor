using Azure.Messaging;
using Azure;
using Microsoft.Identity.Client;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RssVideoProcessor.Schemas;
using System.Text;
using RssVideoProcessor.Schemas;

public class AzureOpenAIService
{
    // Similar to the CorrectnessPrompt below, this will measure correctness of the LLM response, but it will compare this against a ground truth response for a known video so it can tell whether or not
    // the proper decisions were pulled out of the video by the LLM.
    private string UnitTestCorrectnessPrompt = @"You are an AI evaluator.
        The 'correctness metric' is a measure of if the generated answer is correct based on the ground truth answer. You will be given the generated answer and the ground truth answer, both of which will be a JSON list of extracted decisions in the following format:

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
        }

        You need to compare each extracted_decision item from both the generated answer and ground truth answer and score each extracted_decision of the answer between one to five using the following rating scale:
        One: The answer is incorrect
        Three: The answer is partially correct, but could be missing some key context or nuance tha tmakes it potentially misleading or incomplete compared to the context provided.
        Five: The answer is correct and complete based on the context provided.

        You must also provide your reasoning as to why the rating you selected was given.

        The rating value should always be either 1, 3, or 5.

        You will add your thoughts and rating for each key_decision into the key_decision JSON and return the JSON as the response.

        If a key decision is present in the ground truth and is missing from the generated answer, you must add a key_decision with this ground truth, rate it as a 1, and explain in the thoughts that it is missing from the generated answer.
        If a key decision is present in the generated answer and is missing from the ground truth, you must rate it as a 1, and explain in the thoughts that it is missing from the ground truth.

        
        question: Using the provided context, please scan the content to determine if any key decisions were made.
        ground_truth: {ground_truth}
        answer: {answer}
        ";

    // This prompt will be used to determine correctness of the LLM response by generating a rating and throughts for each key decision extracted from the answer.
    private string CorrectnessPrompt = @"You are an AI evaluator.
        The 'correctness metric' is a measure of if the generated answer is correct based on the context provided. The generated answer will be a JSON list of extracted decisions in the following format:

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
        }

        You will need to compare each extracted_decision item from the answer to the context provided and score each extracted_decision item between one to five using the following rating scale:
        One: The answer is incorrect
        Three: The answer is partially correct, but could be missing some key context or nuance tha tmakes it potentially misleading or incomplete compared to the context provided.
        Five: The answer is correct and complete based on the context provided.

        You must also provide your reasoning as to why the rating you selected was given.

        The rating value should always be either 1, 3, or 5.

        You will add your thoughts and rating for each key_decision into the key_decision JSON and return the JSON as the response.

        
        question: Using the provided context, please scan the content to determine if any key decisions were made.
        context: {context}
        answer: {answer}
        ";

    private const string SystemPrompt = @"
        You are an AI assistant that analyzes insights from a video and extracts important moments that occurred made in the meetings from the speakers to include key decisions made as well as any heightened emotions such as excitement, anger, or sadness.
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

        Each node of the JSON is a section extracted from the video analysis. If a key decision is made during the meeting, you will add this to a list of JSON items for each key decision.";

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

    /// <summary>
    /// Uses the LLM to scan the entire prompt content from a video and determine if any key decisions were made. If
    /// we are running a unit test (pre-production) where we have ground truth data to compare the answers again, we
    /// will not run any validation in this method, so no score or thoughts will be returned in the JSON response.
    /// </summary>
    /// <param name="promptContent">The full PromptContent from video indexer for the given video</param>
    /// <param name="runUnitTest">Whether we are running a unit test against ground truth data for a known video</param>
    /// <returns></returns>
    public async Task<string> GetChatResponseAsync(string promptContent, string runUnitTest)
    {
        using HttpClient client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Add("api-key", _apiKey);

        // Define the request payload for a chat completion
        var requestPayload = new
        {
            messages = new[]
            {
                new { role = "system", content = $"{SystemPrompt}\n\n Only use the provided context, do not reply otherwise. Only return properly structured JSON as the response. Context: {promptContent}" },
                new { role = "user", content = "Using the provided context, please scan the content to determine if any key decisions were made." }
            },
            max_tokens = 4096,  // Define the maximum number of tokens
            temperature = 0.7,  // Optional, controls randomness of the response
            response_format = new
            {
                type = "json_schema",
                json_schema = JObject.Parse(_schemaLoader.LoadSchema("Insights.json"))
            },
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

        // only generate the validation response if we are not running a unit test
        if (runUnitTest == "0" || string.IsNullOrWhiteSpace(runUnitTest))
        {
            messageContent = await RunValidationAsync(promptContent, messageContent);
        }

        return messageContent;
    }

    /// <summary>
    /// Runs a validation, using the LLM as a judge, to comapre the answer generated by the LLM to the context provided,
    /// where the context is the promptcontent from video indexer.
    /// </summary>
    /// <param name="context">The full PromptContent data from Video Indexer</param>
    /// <param name="answer">The answer the LLM generated when identifying key points in the video</param>
    /// <returns></returns>
    public async Task<string> RunValidationAsync(string context, string answer)
    {
        using HttpClient client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Add("api-key", _apiKey);

        // Testing bad response
        /*answer = @"
        {  
          ""extracted_decision"": [  
            {  
              ""start"": ""0:07:58.48"",  
              ""end"": ""0:10:31.48"",  
              ""key_decision"": ""Council Member Gilmore said hello.""  
            },  
            {  
              ""start"": ""0:16:58.2"",  
              ""end"": ""0:19:24.8"",  
              ""key_decision"": ""Council Member Lewis ate an apple""  
            },  
            {  
              ""start"": ""0:26:29.8"",  
              ""end"": ""0:30:59.56"",  
              ""key_decision"": ""The lights went out.""  
            }
          ]  
        }  
        ";*/

        CorrectnessPrompt = CorrectnessPrompt.Replace("{context}", context);
        CorrectnessPrompt = CorrectnessPrompt.Replace("{answer}", answer);

        var requestPayload = new
        {
            messages = new[]
            {
                new { role = "system", content = $"You are an AI assistant evaluating the quality of answers." },
                new { role = "user", content = CorrectnessPrompt }
            },
            max_tokens = 4096,  // Define the maximum number of tokens
            temperature = 0.7,  // Optional, controls randomness of the response
            response_format = new
            {
                type = "json_schema",
                json_schema = JObject.Parse(_schemaLoader.LoadSchema("Validation.json"))
            },
        };

        // Serialize the payload to JSON
        var jsonPayload = JsonConvert.SerializeObject(requestPayload);
        var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

        var response = await client.PostAsync(_azureOpenAIUrl, content);

        // Get the response content
        var responseContent = await response.Content.ReadAsStringAsync();

        // Parse the JSON response
        JObject jsonResponse = JObject.Parse(responseContent);

        // Extract the content from the message inside choices[0]
        var messageContent = jsonResponse["choices"]?[0]?["message"]?["content"]?.ToString();

        return messageContent;
    }

    /// <summary>
    /// This method will be used to validate the correctness of the LLM response by comparing it to a ground truth response for a known video.
    /// </summary>
    /// <param name="answer">The JSON representation of the LLM response</param>
    /// <returns></returns>
    public async Task<string> RunUnitTestValidationAsync(string answer)
    {
        // load the ground truth answers from a given JSON file
        string groundTruthFileName = Environment.GetEnvironmentVariable("GroundTruthFile", EnvironmentVariableTarget.Process);
        string groundTruthData = File.ReadAllText(groundTruthFileName);
        JObject groundTruthDataJson = JObject.Parse(groundTruthData);        

        using HttpClient client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Add("api-key", _apiKey);

        UnitTestCorrectnessPrompt = UnitTestCorrectnessPrompt.Replace("{ground_truth}", groundTruthDataJson.ToString());
        UnitTestCorrectnessPrompt = UnitTestCorrectnessPrompt.Replace("{answer}", answer);

        var requestPayload = new
        {
            messages = new[]
            {
                new { role = "system", content = $"You are an AI assistant evaluating the correctness of answers." },
                new { role = "user", content = UnitTestCorrectnessPrompt }
            },
            max_tokens = 4096,  // Define the maximum number of tokens
            temperature = 0.7,  // Optional, controls randomness of the response
            response_format = new
            {
                type = "json_schema",
                json_schema = JObject.Parse(_schemaLoader.LoadSchema("Validation.json"))
            }
        };

        // Serialize the payload to JSON
        var jsonPayload = JsonConvert.SerializeObject(requestPayload);
        var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

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
            tasks.Add(GetChatResponseAsync(chunk, string.Empty));
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