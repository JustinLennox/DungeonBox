// using UnityEngine;
// using UnityEngine.Networking;
// using System.Collections.Generic;
// using System.Threading.Tasks;
// using UnityEngine.UI;
// using System.Collections;
// using System.Text;
// using System;
// using System.Linq;

// // Firebase imports
// using Firebase;
// using Firebase.Database;
// using Firebase.Extensions;

// public enum GameState
// {
//     PreGame,
//     Lobby,
//     SubmittingAnswers,
//     Voting,
//     FinishedVoting,
//     GameOver
// }

// public class GameManager : MonoBehaviour
// {
//     [Header("UI References")]
//     [SerializeField] private GameObject mainMenuPanel;
//     [SerializeField] private GameObject lobbyPanel;
//     [SerializeField] private GameObject gameplayPanel;
//     [SerializeField] private GameObject votingPanel;
//     [SerializeField] private GameObject resultsPanel;
//     [SerializeField] private Text gameCodeText;
//     [SerializeField] private Text promptText;
//     [SerializeField] private Text timerText;
//     [SerializeField] private RawImage qrCodeImage;
//     [SerializeField] private GridLayoutGroup playerGrid;
//     [SerializeField] private PlayerSlot[] playerSlots;

//     private GameState currentState;
//     private string gameCode;
//     private float timer;
//     private string currentPrompt;
//     private Dictionary<string, Player> players = new Dictionary<string, Player>();
//     private List<Answer> currentAnswers = new List<Answer>();

//     private readonly string BEDROCK_ENDPOINT = "https://bedrock-runtime.us-west-2.amazonaws.com";
//     private readonly string MODEL_ID = "anthropic.claude-v2";
//     private readonly string API_ENDPOINT = "https://your-api-endpoint.com"; // Your AppSync/API Gateway endpoint

//     // Firebase references
//     private DatabaseReference dbRootRef;

//     private void Start()
//     {
//         // Initialize Firebase first
//         FirebaseApp.CheckAndFixDependenciesAsync().ContinueWithOnMainThread(task =>
//         {
//             var dependencyStatus = task.Result;
//             if (dependencyStatus == DependencyStatus.Available)
//             {
//                 InitializeFirebase();
//                 SetGameState(GameState.PreGame);
//             }
//             else
//             {
//                 Debug.LogError($"Could not resolve all Firebase dependencies: {dependencyStatus}");
//             }
//         });
//     }

//     private void InitializeFirebase()
//     {
//         dbRootRef = FirebaseDatabase.DefaultInstance.RootReference;
//     }

//     public void OnPlayButtonClicked()
//     {
//         CreateGameSession();
//     }

//     private async void CreateGameSession()
//     {
//         try
//         {
//             gameCode = GenerateGameCode();
//             gameCodeText.text = gameCode;

//             // Example: create a game node in Firebase Realtime Database
//             // with some basic properties like maxPlayers, currentState, etc.
//             var newGameData = new Dictionary<string, object>
//             {
//                 { "gameCode", gameCode },
//                 { "maxPlayers", 8 },
//                 { "state", GameState.Lobby.ToString() }
//             };

//             await dbRootRef
//                 .Child("games")
//                 .Child(gameCode)
//                 .SetValueAsync(newGameData);

//             SetGameState(GameState.Lobby);
//             GenerateQRCode();
//             StartBedrockChat();

//             // Start listening for real-time events (player joins, answers, etc.)
//             SetupRealtimeListeners(gameCode);
//         }
//         catch (Exception e)
//         {
//             Debug.LogError($"Failed to create game session: {e.Message}");
//         }
//     }

//     private void SetupRealtimeListeners(string code)
//     {
//         // Listen for new players
//         FirebaseDatabase.DefaultInstance
//             .GetReference("games")
//             .Child(code)
//             .Child("players")
//             .ChildAdded += HandlePlayerAdded;

//         // Listen for new answers
//         FirebaseDatabase.DefaultInstance
//             .GetReference("games")
//             .Child(code)
//             .Child("answers")
//             .ChildAdded += HandleAnswerAdded;

//         // Listen for votes
//         FirebaseDatabase.DefaultInstance
//             .GetReference("games")
//             .Child(code)
//             .Child("answers")
//             .ChildChanged += HandleAnswerChanged;

//         // If you also want to listen for state changes from other clients:
//         FirebaseDatabase.DefaultInstance
//             .GetReference("games")
//             .Child(code)
//             .Child("state")
//             .ValueChanged += HandleStateChanged;
//     }

//     #region Firebase Event Handlers

//     private void HandlePlayerAdded(object sender, ChildChangedEventArgs args)
//     {
//         if (args.Snapshot != null && args.Snapshot.Value != null)
//         {
//             var data = (Dictionary<string, object>)args.Snapshot.Value;
            
//             string playerId = data.ContainsKey("playerId") ? data["playerId"].ToString() : "";
//             string playerName = data.ContainsKey("playerName") ? data["playerName"].ToString() : "";

//             // Double-check we haven't already added this player
//             if (!players.ContainsKey(playerId))
//             {
//                 OnPlayerJoined(playerId, playerName);
//             }
//         }
//     }

//     private void HandleAnswerAdded(object sender, ChildChangedEventArgs args)
//     {
//         if (args.Snapshot != null && args.Snapshot.Value != null)
//         {
//             var data = (Dictionary<string, object>)args.Snapshot.Value;

//             string answerId = data.ContainsKey("id") ? data["id"].ToString() : "";
//             string playerId = data.ContainsKey("playerId") ? data["playerId"].ToString() : "";
//             string content = data.ContainsKey("content") ? data["content"].ToString() : "";

//             // Check if we already have this answer in our list
//             bool alreadyExists = currentAnswers.Any(a => a.Id == answerId);
//             if (!alreadyExists)
//             {
//                 // Add to local list
//                 currentAnswers.Add(new Answer(playerId, content) { Id = answerId });
//                 OnAnswerSubmitted(playerId, content);
//             }
//         }
//     }

//     private void HandleAnswerChanged(object sender, ChildChangedEventArgs args)
//     {
//         // We can detect if the 'votes' field changed
//         // This can help us handle OnVoteSubmitted logic
//         if (args.Snapshot != null && args.Snapshot.Value != null)
//         {
//             var data = (Dictionary<string, object>)args.Snapshot.Value;

//             string answerId = data.ContainsKey("id") ? data["id"].ToString() : "";
//             int votes = data.ContainsKey("votes") ? Convert.ToInt32(data["votes"]) : 0;

//             var answer = currentAnswers.FirstOrDefault(a => a.Id == answerId);
//             if (answer != null)
//             {
//                 // In Firebase approach, you might also store the last voter or something similar
//                 // For simplicity, let's just update the local votes count
//                 answer.Votes = votes;
//             }
//         }
//     }

//     private void HandleStateChanged(object sender, ValueChangedEventArgs args)
//     {
//         if (args.Snapshot != null && args.Snapshot.Value != null)
//         {
//             string newStateString = args.Snapshot.Value.ToString();
//             if (Enum.TryParse(newStateString, out GameState newState))
//             {
//                 // This means someone updated the game state in Firebase from another client
//                 // You can optionally choose to apply it here:
//                 SetGameState(newState);
//             }
//         }
//     }

//     #endregion

//     #region Player, Answer, and Vote Methods

//     public void OnPlayerJoined(string playerId, string playerName)
//     {
//         if (!players.ContainsKey(playerId) && players.Count < 8)
//         {
//             players.Add(playerId, new Player(playerId, playerName));
//             UpdatePlayerSlots();
//         }
//     }

//     public void OnAnswerSubmitted(string playerId, string answer)
//     {
//         UpdatePlayerAnswerStatus(playerId);
//     }

//     public async void OnVoteSubmitted(string playerId, string answerId)
//     {
//         // Increment vote count in Firebase
//         DatabaseReference answerRef = dbRootRef
//             .Child("games")
//             .Child(gameCode)
//             .Child("answers")
//             .Child(answerId);

//         // Transaction to safely increment
//         await answerRef.RunTransaction(mutableData =>
//         {
//             var dict = mutableData.Value as Dictionary<string, object>;
//             if (dict == null) return TransactionResult.Success(mutableData);

//             if (!dict.ContainsKey("votes")) dict["votes"] = 0;
//             dict["votes"] = Convert.ToInt32(dict["votes"]) + 1;

//             mutableData.Value = dict;
//             return TransactionResult.Success(mutableData);
//         });

//         // Mark the player as having voted locally
//         if (players.TryGetValue(playerId, out Player p))
//         {
//             p.HasVoted = true;
//         }
//     }

//     #endregion

//     #region Bedrock / Prompt

//     private async void StartBedrockChat()
//     {
//         try
//         {
//             await GetNextPrompt();
//         }
//         catch (Exception e)
//         {
//             Debug.LogError($"Failed to start chat: {e.Message}");
//         }
//     }

//     private async Task<string> GetBedrockResponse(string prompt)
//     {
//         var requestBody = new
//         {
//             prompt = $"Human: Generate a creative DnD scenario for players to respond to.\n\nAssistant: Let me create an engaging DnD scenario for your game.",
//             max_tokens = 500,
//             temperature = 0.7
//         };

//         string jsonBody = JsonUtility.ToJson(requestBody);
//         string requestUrl = $"{BEDROCK_ENDPOINT}/model/{MODEL_ID}/invoke";

//         using (UnityWebRequest request = new UnityWebRequest(requestUrl, "POST"))
//         {
//             byte[] bodyRaw = Encoding.UTF8.GetBytes(jsonBody);
//             request.uploadHandler = new UploadHandlerRaw(bodyRaw);
//             request.downloadHandler = new DownloadHandlerBuffer();
//             request.SetRequestHeader("Content-Type", "application/json");
//             request.SetRequestHeader("Authorization", GenerateAWSSignature(request, bodyRaw));

//             await request.SendWebRequest();

//             if (request.result == UnityWebRequest.Result.Success)
//             {
//                 var response = JsonUtility.FromJson<BedrockResponse>(request.downloadHandler.text);
//                 return response.completion;
//             }

//             Debug.LogError($"Error: {request.error}");
//             return "Failed to generate prompt. Using fallback: You enter a mysterious dungeon...";
//         }
//     }

//     private async Task GetNextPrompt()
//     {
//         try
//         {
//             currentPrompt = await GetBedrockResponse("Generate a DnD scenario");
//             promptText.text = currentPrompt;
//         }
//         catch (Exception e)
//         {
//             Debug.LogError($"Failed to get prompt: {e.Message}");
//             promptText.text = "Failed to get prompt. Please try again.";
//         }
//     }

//     #endregion

//     #region Game State

//     private string GenerateGameCode()
//     {
//         const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
//         char[] code = new char[4];
//         for (int i = 0; i < 4; i++)
//         {
//             code[i] = chars[UnityEngine.Random.Range(0, chars.Length)];
//         }
//         return new string(code);
//     }

//     private void GenerateQRCode()
//     {
//         string url = $"https://your-game-url.com/join/{gameCode}";
//         // Use a QR code generation library here
//         // Set the generated texture to qrCodeImage
//     }

//     private void SetGameState(GameState newState)
//     {
//         currentState = newState;
//         mainMenuPanel.SetActive(newState == GameState.PreGame);
//         lobbyPanel.SetActive(newState == GameState.Lobby);
//         gameplayPanel.SetActive(newState == GameState.SubmittingAnswers);
//         votingPanel.SetActive(newState == GameState.Voting);
//         resultsPanel.SetActive(newState == GameState.FinishedVoting);

//         switch (newState)
//         {
//             case GameState.SubmittingAnswers:
//                 StartCoroutine(SubmittingAnswersPhase());
//                 break;
//             case GameState.Voting:
//                 StartCoroutine(VotingPhase());
//                 break;
//             case GameState.FinishedVoting:
//                 StartCoroutine(FinishedVotingPhase());
//                 break;
//         }

//         NotifyStateChange(newState);
//     }

//     private async void NotifyStateChange(GameState state)
//     {
//         // Update the "state" field in Firebase
//         await dbRootRef
//             .Child("games")
//             .Child(gameCode)
//             .Child("state")
//             .SetValueAsync(state.ToString());
//     }

//     private IEnumerator SubmittingAnswersPhase()
//     {
//         timer = 30f;
//         while (timer > 0)
//         {
//             timer -= Time.deltaTime;
//             timerText.text = $"Time remaining: {Mathf.CeilToInt(timer)}";
//             yield return null;
//         }
//         SetGameState(GameState.Voting);
//     }

//     private IEnumerator VotingPhase()
//     {
//         timer = 30f;
//         while (timer > 0 && !AllPlayersVoted())
//         {
//             timer -= Time.deltaTime;
//             timerText.text = $"Time remaining: {Mathf.CeilToInt(timer)}";
//             yield return null;
//         }
//         SetGameState(GameState.FinishedVoting);
//     }

//     private IEnumerator FinishedVotingPhase()
//     {
//         var topAnswer = GetTopVotedAnswer();
//         if (topAnswer != null)
//         {
//             UpdatePlayerScore(topAnswer.PlayerId);
//         }

//         yield return new WaitForSeconds(10f);

//         // Clear old answers for the next round
//         currentAnswers.Clear();
//         // If you store them in Firebase, also clear them there:
//         await dbRootRef.Child("games").Child(gameCode).Child("answers").RemoveValueAsync();

//         // Retrieve a new prompt
//         await GetNextPrompt();
//         SetGameState(GameState.SubmittingAnswers);
//     }

//     private bool AllPlayersVoted()
//     {
//         // If you rely on local data
//         return players.Values.All(p => p.HasVoted);
//     }

//     private Answer GetTopVotedAnswer()
//     {
//         return currentAnswers.OrderByDescending(a => a.Votes).FirstOrDefault();
//     }

//     private void UpdatePlayerScore(string playerId)
//     {
//         if (players.TryGetValue(playerId, out Player player))
//         {
//             player.Score += 1000;
//             UpdatePlayerUI(player);
//         }
//     }

//     #endregion

//     #region UI Helpers

//     private void UpdatePlayerSlots()
//     {
//         var playerArray = players.Values.ToArray();
//         for (int i = 0; i < playerSlots.Length; i++)
//         {
//             if (i < playerArray.Length)
//             {
//                 var player = playerArray[i];
//                 playerSlots[i].SetPlayer(player);
//             }
//             else
//             {
//                 playerSlots[i].Clear();
//             }
//         }
//     }

//     private void UpdatePlayerAnswerStatus(string playerId)
//     {
//         var playerSlot = Array.Find(playerSlots, slot => slot.PlayerId == playerId);
//         if (playerSlot != null)
//         {
//             playerSlot.SetAnswerStatus(true);
//         }
//     }

//     private void UpdatePlayerUI(Player player)
//     {
//         var slot = Array.Find(playerSlots, s => s.PlayerId == player.Id);
//         if (slot != null)
//         {
//             slot.SetPlayer(player);
//         }
//     }

//     #endregion

//     #region AWS Signature (Stub)

//     private string GenerateAWSSignature(UnityWebRequest request, byte[] bodyRaw)
//     {
//         // AWS Signature V4 generation code
//         // Implementation omitted or replaced with your custom logic
//         return "";
//     }

//     #endregion

//     #region Data Models

//     [System.Serializable]
//     private class BedrockResponse
//     {
//         public string completion;
//         public string stop_reason;
//     }

//     public class Player
//     {
//         public string Id { get; private set; }
//         public string Name { get; private set; }
//         public int Score { get; set; }
//         public bool HasVoted { get; set; }

//         public Player(string id, string name)
//         {
//             Id = id;
//             Name = name;
//             Score = 0;
//             HasVoted = false;
//         }
//     }

//     public class Answer
//     {
//         public string Id { get; set; }
//         public string PlayerId { get; private set; }
//         public string Content { get; private set; }
//         public int Votes { get; set; }

//         public Answer(string playerId, string content)
//         {
//             // We'll overwrite the Id from Firebase data if it’s new
//             Id = Guid.NewGuid().ToString();
//             PlayerId = playerId;
//             Content = content;
//             Votes = 0;
//         }
//     }

//     #endregion
// }
