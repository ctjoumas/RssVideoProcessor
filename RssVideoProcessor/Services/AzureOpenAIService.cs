using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RssVideoProcessor.Schemas;
using System.Text;
using Polly.Retry;
using Polly;

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
    private static readonly SemaphoreSlim semaphore = new SemaphoreSlim(5); // Limit concurrency to 5 requests
    private readonly HttpClient _retryClient;

    // Retry policy with exponential backoff using Polly
    private static readonly AsyncRetryPolicy<HttpResponseMessage> retryPolicy = Policy
        .HandleResult<HttpResponseMessage>(response => response.StatusCode == (System.Net.HttpStatusCode)429) // Handle 429 responses
        .WaitAndRetryAsync(
            retryCount: 5, // Retry up to 5 times
            sleepDurationProvider: retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)), // Exponential backoff
            onRetry: (outcome, timespan, retryAttempt, context) =>
            {
                Console.WriteLine($"Retrying... Attempt: {retryAttempt} after {timespan.Seconds} seconds due to {outcome.Result.StatusCode}");
            }
        );

    public AzureOpenAIService(IHttpClientFactory httpClientFactory)
    {
        _apiKey = Environment.GetEnvironmentVariable("AzureOpenAIKey", EnvironmentVariableTarget.Process);
        var endPoint = Environment.GetEnvironmentVariable("AzureOpenAIEndpoint", EnvironmentVariableTarget.Process);
        var modelName = Environment.GetEnvironmentVariable("ModelName", EnvironmentVariableTarget.Process);
        var apiVersion = Environment.GetEnvironmentVariable("ApiVersion", EnvironmentVariableTarget.Process);
        _httpClientFactory = httpClientFactory;
        _azureOpenAIUrl = $"{endPoint}{modelName}/chat/completions?api-version={apiVersion}";
        _schemaLoader = new SchemaLoader(@".\Schemas");

        using HttpClient _retryClient = _httpClientFactory.CreateClient();
        _retryClient.DefaultRequestHeaders.Add("api-key", _apiKey);
        _retryClient.Timeout = TimeSpan.FromSeconds(60);
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
                //new { role = "user", content = "Using the provided context, please scan the content to determine if any key decisions were made." }
                new { role = "user", content = "Using the provided context, please identify and include any resolutions or bills that are voted on, regardless of the outcome (e.g., adopted, postponed, amended), as key decisions in the analysis. Ensure that all formal actions taken by the council are captured in the extracted decisions. Also identify any highly emotional moments, including those regarding logistics and processes of the meeting." }
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

    public async Task<string> ParallelCallsWithRetryAsync(JObject sectionsJson)
    {
        var sectionsArray = sectionsJson["sections"] as JArray;

        if (sectionsArray == null)
        {
            throw new ArgumentException("Invalid JSON format: 'sections' not found or not an array.");
        }

        var tasks = sectionsArray.Select(async section =>
        {
            // Create a new JObject with the "sections" key to pass the correct structure
            var sectionRequest = new JObject
            {
                ["sections"] = new JArray(section) // Keep "sections" key, but only pass one section
            };

            // Call the retry method with the full "sections" structure
            var response = await CallOpenAIWithRetryAsync(sectionRequest.ToString());
            return response;
        }).ToList();

        // Wait for all tasks to complete and collect the response content
        string[] responseContents = await Task.WhenAll(tasks);

        List<ExtractedDecision> mergedDecisions = new List<ExtractedDecision>();

        foreach (var jsonInput in responseContents)
        {
            var decisionContainer = JsonConvert.DeserializeObject<DecisionContainer>(jsonInput);
            if (decisionContainer != null && decisionContainer.ExtractedDecision != null)
            {
                mergedDecisions.AddRange(decisionContainer.ExtractedDecision);
            }
        }

        // Now we create the final container with all decisions
        var finalContainer = new DecisionContainer
        {
            ExtractedDecision = mergedDecisions
        };

        string finalJson = JsonConvert.SerializeObject(finalContainer, Formatting.Indented);
        
        return finalJson;
    }

    // Method to call Azure OpenAI API with retry policy and semaphore
    private async Task<string> CallOpenAIWithRetryAsync(string requestBody)
    {
        // Ensure semaphore is in place for controlling concurrency
        await semaphore.WaitAsync();

        try
        {
            // Wrap the API call with the retry policy to handle transient errors
            var httpResponse = await retryPolicy.ExecuteAsync(async () =>
            {
                using HttpClient client = _httpClientFactory.CreateClient();
                client.DefaultRequestHeaders.Add("api-key", _apiKey);

                var requestPayload = new
                {
                    messages = new[]
                    {
                    new { role = "system", content = $"{SystemPrompt}\n\nOnly use the provided context, do not reply otherwise. Only return properly structured JSON as the response. Context: {requestBody}" },
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

                var jsonPayload = JsonConvert.SerializeObject(requestPayload);
                var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                var response = await client.PostAsync(_azureOpenAIUrl, content);

                return response;
            });

            // Now process the HttpResponseMessage to extract the response content
            var responseContent = await httpResponse.Content.ReadAsStringAsync();

            // Parse the JSON response
            JObject jsonResponse = JObject.Parse(responseContent);

            // Extract the content from the message inside choices[0]
            var messageContent = jsonResponse["choices"]?[0]?["message"]?["content"]?.ToString();

            return messageContent;
        }
        finally
        {
            // Release the semaphore once the request is complete
            semaphore.Release();
        }
    }

    internal class ExtractedDecision
    {
        [JsonProperty("start")]
        public string? start { get; set; }
        [JsonProperty("end")]
        public string? end { get; set; }
        [JsonProperty("key_decision")]
        public string? key_decision { get; set; }
    }

    internal class DecisionContainer
    {
        [JsonProperty("extracted_decision")]
        public List<ExtractedDecision>? ExtractedDecision { get; set; }
    }
}