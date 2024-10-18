// Required namespaces for the application
using System.Net;
using Azure;
using Azure.AI.FormRecognizer.DocumentAnalysis;
using Azure.AI.OpenAI;
using Azure.Communication;
using Azure.Communication.CallAutomation;
using Azure.Messaging;
using Newtonsoft.Json; 
using System.Net.Http;
using System.Text;
using System.Collections.Generic;
// Create a new web application builder
var builder = WebApplication.CreateBuilder(args);
// Retrieve the application configuration
var configuration = builder.Configuration;
// Get various configuration settings from the environment, or throw exceptions if they're not set
var AZURE_COG_SERVICES_KEY = configuration["AZURE_COG_SERVICES_KEY"] ?? throw new Exception("AZURE_COG_SERVICES_KEY is not set");
var AZURE_COG_SERVICES_ENDPOINT = configuration["AZURE_COG_SERVICES_ENDPOINT"] ?? throw new Exception("AZURE_COG_SERVICES_ENDPOINT is not set");
var ACS_CONNECTION_STRING = configuration["ACS_CONNECTION_STRING"] ?? throw new Exception("ACS_CONNECTION_STRING is not set");
var ACS_PHONE_NUMBER = configuration["ACS_PHONE_NUMBER"] ?? throw new Exception("ACS_PHONE_NUMBER is not set");
var OPENAI_ENDPOINT = configuration["OPENAI_ENDPOINT"] ?? throw new Exception("OPENAI_ENDPOINT is not set");
var OPENAI_KEY = configuration["OPENAI_KEY"] ?? throw new Exception("OPENAI_KEY is not set");
var OPENAI_DEPLOYMENT_NAME = configuration["OPENAI_DEPLOYMENT_NAME"] ?? throw new Exception("OPENAI_DEPLOYMENT_NAME is not set");
var HOST_NAME = configuration["HOST_NAME"] ?? throw new Exception("HOST_NAME is not set");
// Create a new call automation client using the Azure Communication Service connection string
var callClient = new CallAutomationClient(ACS_CONNECTION_STRING);
// Create a dictionary to store chat sessions
var chatSessions = new Dictionary<string, List<ChatMessage>>();
List<string> dataList = new List<string>();
// Register the call client and chat sessions as singletons with the DI container
builder.Services.AddSingleton(callClient);
builder.Services.AddSingleton(chatSessions);
var app = builder.Build();
app.UseDefaultFiles(); 
app.UseStaticFiles();
app.MapPost("/api/generate_prompt", async context =>
{
    // Retrieve the uploaded file from the request
    var file = context.Request.Form.Files[0];
    using var reader = new StreamReader(file.OpenReadStream());
    // Set up the Azure Cognitive Services Document Analysis client
    AzureKeyCredential credential = new AzureKeyCredential(AZURE_COG_SERVICES_KEY);
    DocumentAnalysisClient docClient = new DocumentAnalysisClient(new Uri(AZURE_COG_SERVICES_ENDPOINT), credential);
    // Analyze the document
    AnalyzeDocumentOperation operation = await docClient.AnalyzeDocumentAsync(
        WaitUntil.Completed, "prebuilt-read", reader.BaseStream);
    AnalyzeResult result = operation.Value;
    // Construct the prompt for the chatbot
    // string prompt = @"You are a receptionist at a health clinic whose primary goal is to help me, 
    // a patient, fill out their patient intake forms. You are friendly and concise. 
    // For each question in the form ask me a question get the information, then wait for my answer. 
    // For example, start 'What is you name?', then I will response 'John Smith',  
    // then ask  'What is your date of birth?' and I will respond, 'Jan 4, 1999'.\n\nPATIENT INTAKE FORM:\n\n";
    string prompt = @"You are a customer service representative for a credit card company, helping me with my credit card inquiry. You are polite, professional, and concise. For each question, ask for the necessary information, wait for my response, and then proceed to the next question. Start with, 'Can I have your full name, please?', then wait for my response, and continue asking the following questions one at a time.'.\n\nCredit Card Enquiry :\n\n";
    
    prompt += result.Content;
    // Send the constructed prompt as the response
    await context.Response.WriteAsync(prompt);
});
app.MapPost("/api/call", async context =>
{
    // Deserialize the request body into a CallRequest object
    Console.WriteLine("calling the number");
    var data = await context.Request.ReadFromJsonAsync<CallRequest>();
    if (data == null) return;
    // Set up a call invite
    var callInvite = new CallInvite(
        new PhoneNumberIdentifier(data.PhoneNumber),
        new PhoneNumberIdentifier(ACS_PHONE_NUMBER)
    );
     // Generate a unique ID for the chat session
    var contextId = Guid.NewGuid().ToString();
    var messages = new[] {
        new ChatMessage(ChatRole.System, data.Prompt)
    };
    // Store the messages associated with the chat session
    chatSessions[contextId] = messages.ToList();   
    // Set up call options
    // var createCallOptions = new CreateCallOptions(callInvite, 
    //     new Uri($"{HOST_NAME}/api/callbacks/{contextId}?callerId={WebUtility.UrlEncode(data.PhoneNumber)}"))
    // {
    //     CognitiveServicesEndpoint = new Uri(AZURE_COG_SERVICES_ENDPOINT),
    // };
    var createCallOptions = new CreateCallOptions(callInvite, new Uri($"{HOST_NAME}/api/callbacks/{contextId}?callerId={WebUtility.UrlEncode(data.PhoneNumber)}")) {
  CallIntelligenceOptions = new CallIntelligenceOptions() {
    CognitiveServicesEndpoint = new Uri(AZURE_COG_SERVICES_ENDPOINT)
  }
};
    // Create the call
    var result = await callClient.CreateCallAsync(createCallOptions); 
    Console.WriteLine($"Call created successfully: {result}");
});
// app.MapPost("/api/callbacks/{contextId}", async (context) =>
// {
//         // Parse incoming cloud events
//     var cloudEvents = await context.Request.ReadFromJsonAsync<CloudEvent[]>() ?? Array.Empty<CloudEvent>();
//     var contextId = context.Request.RouteValues["contextId"]?.ToString() ?? "";
//     var callerId = context.Request.Query["callerId"].ToString() ?? "";
//     foreach (var cloudEvent in cloudEvents)
//     {
//         // Parse the cloud event to get the call event details
//         CallAutomationEventBase callEvent = CallAutomationEventParser.Parse(cloudEvent);
//         var callConnection = callClient.GetCallConnection(callEvent.CallConnectionId);
//         var callConnectionMedia = callConnection.GetCallMedia();
//         var messages = chatSessions[contextId];
//         var phoneId = new PhoneNumberIdentifier(callerId); 
//         if (callEvent is CallConnected)
//         {
//             // If the call is connected, get a response from the chatbot and send it to the user
//             var response = await GetChatGPTResponse(messages);
//             messages.Add(new ChatMessage(ChatRole.Assistant, response));
//             await SayAndRecognize(callConnectionMedia, phoneId, response);
//         }
//         if (callEvent is RecognizeCompleted recogEvent 
//             && recogEvent.RecognizeResult is SpeechResult speech_result)
//         {
//             // If speech is recognized, get a response from the chatbot based on the recognized speech and send it to the user
//             messages.Add(new ChatMessage(ChatRole.User, speech_result.Speech));
            
//             var response = await GetChatGPTResponse(messages);
//             messages.Add(new ChatMessage(ChatRole.Assistant, response));
//             await SayAndRecognize(callConnectionMedia, phoneId, response);
//         }                 
//     }   
// });
app.MapPost("/api/callbacks/{contextId}", async (context) =>
{
    Console.WriteLine("callback contextId");
        // Parse incoming cloud events
    var cloudEvents = await context.Request.ReadFromJsonAsync<CloudEvent[]>() ?? Array.Empty<CloudEvent>();
    var contextId = context.Request.RouteValues["contextId"]?.ToString() ?? "";
    var callerId = context.Request.Query["callerId"].ToString() ?? "";
    foreach (var cloudEvent in cloudEvents)
    {
        // Console.WriteLine("cloudEvent"+cloudEvent);
        // Parse the cloud event to get the call event details
        CallAutomationEventBase callEvent = CallAutomationEventParser.Parse(cloudEvent);
        var callConnection = callClient.GetCallConnection(callEvent.CallConnectionId);
        var callConnectionMedia = callConnection.GetCallMedia();
        Console.WriteLine("callEvent,"+CallAutomationEventParser.Parse(cloudEvent));

        var messages = chatSessions[contextId];
        var phoneId = new PhoneNumberIdentifier(callerId); 
        if (callEvent is CallConnected)
        {
            Console.WriteLine("call got connected");
                var dataToSend = new
                {
                item = phoneId.PhoneNumber
                };
                Console.WriteLine("---------data sent format---------");
                Console.WriteLine(JsonConvert.SerializeObject(dataToSend));
                // Set the URL and headers
                var url = "https://script.google.com/macros/s/AKfycbzV3gaQ5P9H9-ZR5_Rf_VWKCJz--15sNVKGQVgORArYKLTB7kYGulDayjmRwskBrfzs/exec";
                var client = new HttpClient();
                var headers = new StringContent(JsonConvert.SerializeObject(dataToSend), Encoding.UTF8, "application/json");
                // Make the POST request
                var push_response = await client.PostAsync(url, headers);
                // Ensure the request was successful
                push_response.EnsureSuccessStatusCode();
            // Read the response content
            var responseBody = await push_response.Content.ReadAsStringAsync();
            Console.WriteLine("Response: " + responseBody);
            // If the call is connected, get a response from the chatbot and send it to the user
            
            var response = await GetChatGPTResponse(messages);
            messages.Add(new ChatMessage(ChatRole.Assistant, response));
            await SayAndRecognize(callConnectionMedia, phoneId, response);
        }
        if (callEvent is RecognizeCompleted recogEvent 
            && recogEvent.RecognizeResult is SpeechResult speech_result)
        {
            string recognizedSpeech = speech_result.Speech.ToLower();
            Console.WriteLine($"Test001: {recognizedSpeech}");
            dataList.Add(recognizedSpeech);
            // Console.WriteLine($"dataList: {dataList}");
            // Convert to lowercase for easier matching
                        // Check if the user said something like "end call"
            if (recognizedSpeech.Contains("end") || recognizedSpeech.Contains("terminate"))
            {
                // Console.WriteLine(  "Ending the call...");
                //         // Print all elements
                // Console.WriteLine("Data collected..");
    
                // foreach (string data in dataList)
                // {
                    
                //     Console.WriteLine(data);
                // }
                // Hang up the call
                await callConnection.HangUpAsync(true);
            }
            //Push details to an external service or database
                var dataToSend = new
                {
                item = recognizedSpeech
                };
                Console.WriteLine("---------data sent format---------");
                Console.WriteLine(JsonConvert.SerializeObject(dataToSend));
                // Set the URL and headers
                var url = "https://script.google.com/macros/s/AKfycbzV3gaQ5P9H9-ZR5_Rf_VWKCJz--15sNVKGQVgORArYKLTB7kYGulDayjmRwskBrfzs/exec";
                var client = new HttpClient();
                var headers = new StringContent(JsonConvert.SerializeObject(dataToSend), Encoding.UTF8, "application/json");
                // Make the POST request
                var push_response = await client.PostAsync(url, headers);
                // Ensure the request was successful
                push_response.EnsureSuccessStatusCode();
            // Read the response content
            if (IsOutOfContext(recognizedSpeech))
            {
                        Console.WriteLine("out of box");
                var outOfContextResponse = "I am here to help with credit card inquiries. How can I assist you with that?";
                await SayAndRecognize(callConnectionMedia, phoneId, outOfContextResponse);
            }
            else{
            var responseBody = await push_response.Content.ReadAsStringAsync();
            Console.WriteLine("Response: " + responseBody);
            messages.Add(new ChatMessage(ChatRole.User, speech_result.Speech));
            // Console.WriteLine("Response: " + responseBody);
            var response = await GetChatGPTResponse(messages);
            messages.Add(new ChatMessage(ChatRole.Assistant, response));
            await SayAndRecognize(callConnectionMedia, phoneId, response);
            }

            
        }
        if (callEvent is CallDisconnected)
        {
        Console.WriteLine("callEvent disconnected by user");
        }
        if (callEvent is RecognizeFailed)
        {
        Console.WriteLine("not able to understand asking it again");
        messages.Add(new ChatMessage(ChatRole.User, "I'm sorry, I couldn't understand that. Could you please repeat?"));
        // Console.WriteLine("Response: " + responseBody);
        var response = await GetChatGPTResponse(messages);
        messages.Add(new ChatMessage(ChatRole.Assistant, response));
        await SayAndRecognize(callConnectionMedia, phoneId, response);
        }

    }   
});
app.Run();
// // // Function to get a response from OpenAI's ChatGPT
async Task<string> GetChatGPTResponse(List<ChatMessage> messages)
{
    Console.WriteLine("In the GPT");
    // Set up the OpenAI client
    OpenAIClient openAIClient = new OpenAIClient(
        new Uri(OPENAI_ENDPOINT),
        new AzureKeyCredential(OPENAI_KEY));
    // Get a chat completion from OpenAI's ChatGPT
    var chatCompletionsOptions = new ChatCompletionsOptions(messages);
    Response<ChatCompletions> response = await openAIClient.GetChatCompletionsAsync(
        deploymentOrModelName: OPENAI_DEPLOYMENT_NAME,
        chatCompletionsOptions);
    Console.WriteLine(response);
    Console.WriteLine("GPT RES",response.Value.Choices[0].Message.Content);
        // Simple out-of-context detection logic
    if (IsOutOfContext(response.Value.Choices[0].Message.Content)) 
    {
        Console.WriteLine("out of box");
        // Respond with a gentle redirect
        return "I'm here to assist with your credit card inquiry. Could you please let me know your question about your credit card?";
    }
    return response.Value.Choices[0].Message.Content;
}
bool IsOutOfContext(string response)
{
    // This is a placeholder logic for detecting out-of-context replies.
    // You can expand this using machine learning models or keyword detection.
    var outOfContextKeywords = new List<string> { "restaurant", "movie", "travel", "weather", "terroism","money laundering" };

    return outOfContextKeywords.Any(keyword => response.Contains(keyword, StringComparison.OrdinalIgnoreCase));
}
// // Function to send a message to the user and recognize their response
async Task SayAndRecognize(CallMedia callConnectionMedia, PhoneNumberIdentifier phoneId, string response)
{
    // Set up the text source for the chatbot's response
    var chatGPTResponseSource = new TextSource(response) {
        VoiceName = "en-US-JennyMultilingualV2Neural"
    };
    // Recognize the user's speech after sending the chatbot's response
    var recognizeOptions =
        new CallMediaRecognizeSpeechOptions(
            targetParticipant: CommunicationIdentifier.FromRawId(phoneId.RawId))
        {
            Prompt = chatGPTResponseSource,
            EndSilenceTimeout = TimeSpan.FromMilliseconds(500)
        };
    var recognize_result = await callConnectionMedia.StartRecognizingAsync(recognizeOptions);
 }
public class CallRequest
{
    public required string PhoneNumber { get; set; }
    public required string Prompt { get; set; }
}
// public class CallIntelligenceOptions
// {
// public Uri CognitiveServicesEndpoint { get; set; }
// }