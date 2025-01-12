using UnityEngine;
using UnityEngine.Networking;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine.UI;
using System.Collections;
using System.Text;
using System;

public enum GameState
{
    PreGame,
    Lobby,
    SubmittingAnswers,
    Voting,
    FinishedVoting,
    GameOver
}

public class GameManager : MonoBehaviour
{
    [Header("UI References")]
    [SerializeField] private GameObject mainMenuPanel;
    [SerializeField] private GameObject lobbyPanel;
    [SerializeField] private GameObject gameplayPanel;
    [SerializeField] private GameObject votingPanel;
    [SerializeField] private GameObject resultsPanel;
    [SerializeField] private Text gameCodeText;
    [SerializeField] private Text promptText;
    [SerializeField] private Text timerText;
    [SerializeField] private RawImage qrCodeImage;
    [SerializeField] private GridLayoutGroup playerGrid;
    [SerializeField] private PlayerSlot[] playerSlots;

    private GameState currentState;
    private string gameCode;
    private float timer;
    private string currentPrompt;
    private Dictionary<string, Player> players = new Dictionary<string, Player>();
    private List<Answer> currentAnswers = new List<Answer>();

    private readonly string BEDROCK_ENDPOINT = "https://bedrock-runtime.us-west-2.amazonaws.com";
    private readonly string MODEL_ID = "anthropic.claude-v2";
    private readonly string API_ENDPOINT = "https://your-api-endpoint.com"; // Your AppSync/API Gateway endpoint

    private WebSocket webSocket;

    private void Start()
    {
        InitializeWebSocket();
        SetGameState(GameState.PreGame);
    }

    private void InitializeWebSocket()
    {
        webSocket = new WebSocket($"wss://your-websocket-endpoint.com");
        webSocket.OnMessage += HandleWebSocketMessage;
        webSocket.Connect();
    }

    private void HandleWebSocketMessage(object sender, MessageEventArgs e)
    {
        var message = JsonUtility.FromJson<WebSocketMessage>(e.Data);
        
        switch (message.type)
        {
            case "PLAYER_JOINED":
                OnPlayerJoined(message.playerId, message.playerName);
                break;
            case "ANSWER_SUBMITTED":
                OnAnswerSubmitted(message.playerId, message.answer);
                break;
            case "VOTE_SUBMITTED":
                OnVoteSubmitted(message.playerId, message.answerId);
                break;
        }
    }

    public void OnPlayButtonClicked()
    {
        CreateGameSession();
    }

    private async void CreateGameSession()
    {
        try
        {
            gameCode = GenerateGameCode();
            
            // Create game session through API
            using (UnityWebRequest request = new UnityWebRequest($"{API_ENDPOINT}/games", "POST"))
            {
                var gameData = new { gameCode = gameCode, maxPlayers = 8 };
                string jsonBody = JsonUtility.ToJson(gameData);
                byte[] bodyRaw = Encoding.UTF8.GetBytes(jsonBody);
                request.uploadHandler = new UploadHandlerRaw(bodyRaw);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");

                await request.SendWebRequest();

                if (request.result == UnityWebRequest.Result.Success)
                {
                    SetGameState(GameState.Lobby);
                    GenerateQRCode();
                    StartBedrockChat();
                }
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"Failed to create game session: {e.Message}");
        }
    }

    private string GenerateGameCode()
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        char[] code = new char[4];
        for (int i = 0; i < 4; i++)
        {
            code[i] = chars[UnityEngine.Random.Range(0, chars.Length)];
        }
        return new string(code);
    }

    private void GenerateQRCode()
    {
        string url = $"https://your-game-url.com/join/{gameCode}";
        // Use a QR code generation library here
        // Set the generated texture to qrCodeImage
    }

    private async void StartBedrockChat()
    {
        try
        {
            await GetNextPrompt();
        }
        catch (Exception e)
        {
            Debug.LogError($"Failed to start chat: {e.Message}");
        }
    }

    private async Task<string> GetBedrockResponse(string prompt)
    {
        var requestBody = new
        {
            prompt = $"Human: Generate a creative DnD scenario for players to respond to.\n\nAssistant: Let me create an engaging DnD scenario for your game.",
            max_tokens = 500,
            temperature = 0.7
        };

        string jsonBody = JsonUtility.ToJson(requestBody);
        string requestUrl = $"{BEDROCK_ENDPOINT}/model/{MODEL_ID}/invoke";

        using (UnityWebRequest request = new UnityWebRequest(requestUrl, "POST"))
        {
            byte[] bodyRaw = Encoding.UTF8.GetBytes(jsonBody);
            request.uploadHandler = new UploadHandlerRaw(bodyRaw);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("Authorization", GenerateAWSSignature(request, bodyRaw));

            await request.SendWebRequest();

            if (request.result == UnityWebRequest.Result.Success)
            {
                var response = JsonUtility.FromJson<BedrockResponse>(request.downloadHandler.text);
                return response.completion;
            }
            
            Debug.LogError($"Error: {request.error}");
            return "Failed to generate prompt. Using fallback: You enter a mysterious dungeon...";
        }
    }

    private async Task GetNextPrompt()
    {
        try
        {
            currentPrompt = await GetBedrockResponse("Generate a DnD scenario");
            promptText.text = currentPrompt;
        }
        catch (Exception e)
        {
            Debug.LogError($"Failed to get prompt: {e.Message}");
            promptText.text = "Failed to get prompt. Please try again.";
        }
    }

    private void SetGameState(GameState newState)
    {
        currentState = newState;
        mainMenuPanel.SetActive(newState == GameState.PreGame);
        lobbyPanel.SetActive(newState == GameState.Lobby);
        gameplayPanel.SetActive(newState == GameState.SubmittingAnswers);
        votingPanel.SetActive(newState == GameState.Voting);
        resultsPanel.SetActive(newState == GameState.FinishedVoting);

        switch (newState)
        {
            case GameState.SubmittingAnswers:
                StartCoroutine(SubmittingAnswersPhase());
                break;
            case GameState.Voting:
                StartCoroutine(VotingPhase());
                break;
            case GameState.FinishedVoting:
                StartCoroutine(FinishedVotingPhase());
                break;
        }

        // Notify backend of state change
        NotifyStateChange(newState);
    }

    private async void NotifyStateChange(GameState state)
    {
        var stateData = new { gameCode = gameCode, state = state.ToString() };
        string jsonBody = JsonUtility.ToJson(stateData);
        
        using (UnityWebRequest request = new UnityWebRequest($"{API_ENDPOINT}/games/{gameCode}/state", "PUT"))
        {
            byte[] bodyRaw = Encoding.UTF8.GetBytes(jsonBody);
            request.uploadHandler = new UploadHandlerRaw(bodyRaw);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");

            await request.SendWebRequest();
        }
    }

    private IEnumerator SubmittingAnswersPhase()
    {
        timer = 30f;
        while (timer > 0)
        {
            timer -= Time.deltaTime;
            timerText.text = $"Time remaining: {Mathf.CeilToInt(timer)}";
            yield return null;
        }
        SetGameState(GameState.Voting);
    }

    private IEnumerator VotingPhase()
    {
        timer = 30f;
        while (timer > 0 && !AllPlayersVoted())
        {
            timer -= Time.deltaTime;
            timerText.text = $"Time remaining: {Mathf.CeilToInt(timer)}";
            yield return null;
        }
        SetGameState(GameState.FinishedVoting);
    }

    private IEnumerator FinishedVotingPhase()
    {
        var topAnswer = GetTopVotedAnswer();
        UpdatePlayerScore(topAnswer.PlayerId);
        yield return new WaitForSeconds(10f);
        await GetNextPrompt();
        SetGameState(GameState.SubmittingAnswers);
    }

    private bool AllPlayersVoted()
    {
        return players.Values.All(p => p.HasVoted);
    }

    private Answer GetTopVotedAnswer()
    {
        return currentAnswers.OrderByDescending(a => a.Votes).First();
    }

    private void UpdatePlayerScore(string playerId)
    {
        if (players.TryGetValue(playerId, out Player player))
        {
            player.Score += 1000;
            UpdatePlayerUI(player);
        }
    }

    public void OnPlayerJoined(string playerId, string playerName)
    {
        if (players.Count < 8)
        {
            players.Add(playerId, new Player(playerId, playerName));
            UpdatePlayerSlots();
        }
    }

    public void OnAnswerSubmitted(string playerId, string answer)
    {
        currentAnswers.Add(new Answer(playerId, answer));
        UpdatePlayerAnswerStatus(playerId);
    }

    public void OnVoteSubmitted(string playerId, string answerId)
    {
        var answer = currentAnswers.Find(a => a.Id == answerId);
        if (answer != null)
        {
            answer.Votes++;
            players[playerId].HasVoted = true;
        }
    }

    private void UpdatePlayerSlots()
    {
        for (int i = 0; i < playerSlots.Length; i++)
        {
            if (i < players.Count)
            {
                var player = players.ElementAt(i).Value;
                playerSlots[i].SetPlayer(player);
            }
            else
            {
                playerSlots[i].Clear();
            }
        }
    }

    private void UpdatePlayerAnswerStatus(string playerId)
    {
        var playerSlot = Array.Find(playerSlots, slot => slot.PlayerId == playerId);
        if (playerSlot != null)
        {
            playerSlot.SetAnswerStatus(true);
        }
    }

    private void OnDestroy()
    {
        webSocket?.Close();
    }

    private string GenerateAWSSignature(UnityWebRequest request, byte[] bodyRaw)
    {
        // AWS Signature V4 generation code (same as previous)
        // ... implementation as shown in previous message
        return ""; // Return the generated signature
    }

    [System.Serializable]
    private class BedrockResponse
    {
        public string completion;
        public string stop_reason;
    }

    [System.Serializable]
    private class WebSocketMessage
    {
        public string type;
        public string playerId;
        public string playerName;
        public string answer;
        public string answerId;
    }
}

public class Player
{
    public string Id { get; private set; }
    public string Name { get; private set; }
    public int Score { get; set; }
    public bool HasVoted { get; set; }

    public Player(string id, string name)
    {
        Id = id;
        Name = name;
        Score = 0;
        HasVoted = false;
    }
}

public class Answer
{
    public string Id { get; private set; }
    public string PlayerId { get; private set; }
    public string Content { get; private set; }
    public int Votes { get; set; }

    public Answer(string playerId, string content)
    {
        Id = System.Guid.NewGuid().ToString();
        PlayerId = playerId;
        Content = content;
        Votes = 0;
    }
}
