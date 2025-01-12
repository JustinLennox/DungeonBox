using UnityEngine;
using UnityEngine.UI;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Firebase;
using Firebase.Database;
using Firebase.Extensions;
using TMPro;

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
    [Header("UI References (Individual Objects)")]
    [SerializeField] private GameObject titleLogo;
    [SerializeField] private GameObject playButton;
    [SerializeField] private TMP_Text gameCodeText;
    [SerializeField] private TMP_Text promptText;
    [SerializeField] private TMP_Text timerText;
    [SerializeField] private Image qrCodeImage;
    [SerializeField] private GridLayoutGroup playerGrid;
    [SerializeField] private PlayerSlot[] playerSlots;

    // Reference to AWSInteractor for AI messaging
    private AWSInteractor awsInteractor;

    private GameState currentState = GameState.PreGame;
    private string gameCode;
    private float timer = 0f;
    private Dictionary<string, Player> players = new Dictionary<string, Player>();
    private List<Answer> currentAnswers = new List<Answer>();

    // Firebase references
    private DatabaseReference dbRootRef;

    private void Start()
    {
        // Grab AWSInteractor
        awsInteractor = GetComponent<AWSInteractor>();
        SetInitialUI();
        if (!awsInteractor)
        {
            Debug.LogError("AWSInteractor component not found on the same GameObject!");
        }

        // Initialize Firebase
        FirebaseApp.CheckAndFixDependenciesAsync().ContinueWithOnMainThread(task =>
        {
            var dependencyStatus = task.Result;
            if (dependencyStatus == DependencyStatus.Available)
            {
                InitializeFirebase();
                SetGameState(GameState.PreGame);
            }
            else
            {
                Debug.LogError($"Could not resolve all Firebase dependencies: {dependencyStatus}");
            }
        });
    }

    /// <summary>
    /// When the game or scene ends, clear out the room.
    /// </summary>
    private async void OnDestroy()
    {
        // If we have a valid gameCode, remove the entire node from Firebase
        if (!string.IsNullOrEmpty(gameCode))
        {
            Debug.Log($"Clearing room {gameCode} from Firebase on destroy...");
            await dbRootRef.Child("games").Child(gameCode).RemoveValueAsync();
        }
    }

    private void InitializeFirebase()
    {
        dbRootRef = FirebaseDatabase.DefaultInstance.RootReference;
        playButton.SetActive(true);
    }

    public void OnPlayButtonClicked()
    {
        // Reset sessionId to empty and send "Start Game" to AI
        awsInteractor.sessionId = "";
        awsInteractor.SendMessageToServer("Start Game");

        CreateGameSession();
    }

    /// <summary>
    /// Creates a new game session in Firebase, initializes a code, sets Lobby state.
    /// </summary>
    private async void CreateGameSession()
    {
        try
        {
            // 1) Generate a room code that isn't taken yet
            gameCode = await GenerateUniqueGameCode();
            gameCodeText.text = gameCode;

            // 2) Create the game data in Firebase
            var newGameData = new Dictionary<string, object>
            {
                { "gameCode", gameCode },
                { "maxPlayers", 8 },
                { "state", GameState.Lobby.ToString() }
            };

            await dbRootRef
                .Child("games")
                .Child(gameCode)
                .SetValueAsync(newGameData);

            // 3) Set local state to Lobby
            SetGameState(GameState.Lobby);
            GenerateQRCode();

            // 4) Start listener for players/answers
            SetupRealtimeListeners(gameCode);
        }
        catch (Exception e)
        {
            Debug.LogError($"Failed to create game session: {e.Message}");
        }
    }

    /// <summary>
    /// Checks if the generated code is already used in /games.
    /// If it is, tries again until it finds an unused code.
    /// </summary>
    private async System.Threading.Tasks.Task<string> GenerateUniqueGameCode()
    {
        while (true)
        {
            string candidate = GenerateRandomCode();
            var snapshot = await dbRootRef.Child("games").Child(candidate).GetValueAsync();
            if (!snapshot.Exists)
            {
                // Code is free to use
                return candidate;
            }
            // Otherwise loop again and generate a new code
        }
    }

    private void SetupRealtimeListeners(string code)
    {
        FirebaseDatabase.DefaultInstance
            .GetReference("games")
            .Child(code)
            .ValueChanged += HandleGameDataChanged;
    }

    private void HandleGameDataChanged(object sender, ValueChangedEventArgs args)
    {
        if (args.Snapshot == null || !args.Snapshot.Exists) return;

        var gameData = args.Snapshot.Value as Dictionary<string, object>;
        if (gameData == null) return;

        // We do NOT parse "gameData.state" at all here (Unity is the authority on state).

        // --- Parse players ---
        if (gameData.ContainsKey("players"))
        {
            var playersData = gameData["players"] as Dictionary<string, object>;
            if (playersData != null)
            {
                players.Clear();

                foreach (var kvp in playersData)
                {
                    var playerDict = kvp.Value as Dictionary<string, object>;
                    if (playerDict == null) continue;

                    string playerId = playerDict.ContainsKey("playerId")
                        ? playerDict["playerId"].ToString()
                        : kvp.Key;
                    string playerName = playerDict.ContainsKey("playerName")
                        ? playerDict["playerName"].ToString()
                        : "Unknown";

                    int score = 0;
                    if (playerDict.ContainsKey("score"))
                    {
                        int.TryParse(playerDict["score"].ToString(), out score);
                    }

                    bool hasVoted = false;
                    if (playerDict.ContainsKey("hasVoted"))
                    {
                        bool.TryParse(playerDict["hasVoted"].ToString(), out hasVoted);
                    }

                    bool startGame = false;
                    if (playerDict.ContainsKey("startGame"))
                    {
                        bool.TryParse(playerDict["startGame"].ToString(), out startGame);
                    }

                    Player p = new Player(playerId, playerName)
                    {
                        Score = score,
                        HasVoted = hasVoted,
                        startGame = startGame
                    };

                    players[playerId] = p;
                }

                UpdatePlayerSlots();

                // If we are in LOBBY, check if any player wants to "startGame"
                if (currentState == GameState.Lobby)
                {
                    bool anyStartGame = players.Values.Any(pl => pl.startGame == true);
                    if (anyStartGame)
                    {
                        Debug.Log("Player requested startGame => moving to SubmittingAnswers");
                        SetGameState(GameState.SubmittingAnswers);
                        ClearStartGameFlags();
                    }
                }
            }
        }

        // --- Parse answers ---
        if (gameData.ContainsKey("answers"))
        {
            var answersData = gameData["answers"] as Dictionary<string, object>;
            if (answersData != null)
            {
                currentAnswers.Clear();
                foreach (var kvp in answersData)
                {
                    var answerDict = kvp.Value as Dictionary<string, object>;
                    if (answerDict == null) continue;

                    string answerId = kvp.Key;
                    string playerId = answerDict.ContainsKey("playerId")
                        ? answerDict["playerId"].ToString()
                        : "";
                    string content = answerDict.ContainsKey("content")
                        ? answerDict["content"].ToString()
                        : "";
                    int votes = 0;
                    if (answerDict.ContainsKey("votes"))
                    {
                        int.TryParse(answerDict["votes"].ToString(), out votes);
                    }

                    var newAnswer = new Answer(playerId, content)
                    {
                        Id = answerId,
                        Votes = votes
                    };
                    currentAnswers.Add(newAnswer);
                }
            }
        }
    }

    private async void ClearStartGameFlags()
    {
        var playerList = players.ToList(); // snapshot
        foreach (var kvp in playerList)
        {
            string playerId = kvp.Key;
            Player p = kvp.Value;
            if (p.startGame)
            {
                p.startGame = false;
                await dbRootRef.Child("games")
                    .Child(gameCode)
                    .Child("players")
                    .Child(playerId)
                    .Child("startGame")
                    .SetValueAsync(false);
            }
        }
    }

    #region Game State / Rounds

    private void SetInitialUI()
    {
        titleLogo.SetActive(true);
        playButton.SetActive(false);

        gameCodeText.gameObject.SetActive(false);
        promptText.gameObject.SetActive(false);
        timerText.gameObject.SetActive(false);
        qrCodeImage.gameObject.SetActive(false);
        playerGrid.gameObject.SetActive(false);

        UpdatePlayerSlots();
    }

    private void SetGameState(GameState newState)
    {
        if (currentState == newState) return;
        currentState = newState;
        Debug.Log($"Game state changed to: {newState}");

        switch (newState)
        {
            case GameState.PreGame:
                break;

            case GameState.Lobby:
                gameCodeText.gameObject.SetActive(true);
                qrCodeImage.gameObject.SetActive(true);
                playerGrid.gameObject.SetActive(true);

                titleLogo.SetActive(true);
                playButton.SetActive(false);
                promptText.gameObject.SetActive(false);
                timerText.gameObject.SetActive(false);
                break;

            case GameState.SubmittingAnswers:
                // 1) Clear old answers & reset hasVoted
                ClearOldAnswersAndResetVotes();

                // 2) Setup UI
                promptText.text = awsInteractor.currentPrompt;
                promptText.gameObject.SetActive(true);
                timerText.gameObject.SetActive(true);
                playerGrid.gameObject.SetActive(true);

                titleLogo.SetActive(false);
                playButton.SetActive(false);
                gameCodeText.gameObject.SetActive(false);
                qrCodeImage.gameObject.SetActive(false);

                // 3) Start round
                StartCoroutine(SubmittingAnswersPhase());
                break;

            case GameState.Voting:
                timerText.gameObject.SetActive(true);
                playerGrid.gameObject.SetActive(true);

                titleLogo.SetActive(false);
                playButton.SetActive(false);
                promptText.gameObject.SetActive(false);
                gameCodeText.gameObject.SetActive(false);
                qrCodeImage.gameObject.SetActive(false);

                StartCoroutine(VotingPhase());
                break;

            case GameState.FinishedVoting:
                promptText.gameObject.SetActive(false);
                titleLogo.SetActive(false);
                playButton.SetActive(false);
                gameCodeText.gameObject.SetActive(false);
                qrCodeImage.gameObject.SetActive(false);
                timerText.gameObject.SetActive(false);
                playerGrid.gameObject.SetActive(true);

                StartCoroutine(FinishedVotingPhase());
                break;
        }

        UpdateFirebaseState(newState);
    }

    /// <summary>
    /// Called immediately before we enter the SubmittingAnswers phase.
    /// Clears previous answers and sets all players hasVoted=false in Firebase.
    /// </summary>
    private async void ClearOldAnswersAndResetVotes()
    {
        // Clear local list
        currentAnswers.Clear();

        // Clear from Firebase
        await dbRootRef.Child("games").Child(gameCode).Child("answers").RemoveValueAsync();

        var playerList = players.ToList(); // snapshot
        // Reset each player's hasVoted = false
        foreach (var kvp in playerList)
        {
            kvp.Value.HasVoted = false;
            var playerId = kvp.Key;
            await dbRootRef.Child("games")
                .Child(gameCode)
                .Child("players")
                .Child(playerId)
                .Child("hasVoted")
                .SetValueAsync(false);
        }
    }
    private IEnumerator SubmittingAnswersPhase()
    {
        timer = 60f;
        // Keep running until either time runs out OR all players have submitted
        while (timer > 0 && !AllPlayersSubmitted())
        {
            timer -= Time.deltaTime;
            timerText.text = $"{Mathf.CeilToInt(timer)}";
            yield return null;
        }

        // Once time is up or all answers are in, transition to Voting
        SetGameState(GameState.Voting);
    }

    // Helper function that checks if each player has submitted exactly 1 answer
    private bool AllPlayersSubmitted()
    {
        // For example, if each player can only submit once:
        return currentAnswers.Count == players.Count;
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
        if (topAnswer != null)
        {
            UpdatePlayerScore(topAnswer.PlayerId);
            awsInteractor.SendMessageToServer(topAnswer.Content);
        }

        yield return new WaitForSeconds(10f);

        // Move to next round
        SetGameState(GameState.SubmittingAnswers);
    }

    private bool AllPlayersVoted()
    {
        return players.Values.All(p => p.HasVoted);
    }

    private Answer GetTopVotedAnswer()
    {
        return currentAnswers.OrderByDescending(a => a.Votes).FirstOrDefault();
    }

    private void UpdatePlayerScore(string playerId)
    {
        if (players.TryGetValue(playerId, out Player player))
        {
            player.Score += 10;
            UpdatePlayerUI(player);

            dbRootRef.Child("games")
                     .Child(gameCode)
                     .Child("players")
                     .Child(playerId)
                     .Child("score")
                     .SetValueAsync(player.Score);
        }
    }

    private async void UpdateFirebaseState(GameState state)
    {
        await dbRootRef
            .Child("games")
            .Child(gameCode)
            .Child("state")
            .SetValueAsync(state.ToString());
    }

    #endregion

    #region UI Helpers

    private void UpdatePlayerSlots()
    {
        var playerArray = players.Values.ToArray();
        for (int i = 0; i < playerSlots.Length; i++)
        {
            if (i < playerArray.Length)
            {
                playerSlots[i].SetPlayer(playerArray[i]);
            }
            else
            {
                playerSlots[i].Clear();
            }
        }
    }

    private void UpdatePlayerUI(Player player)
    {
        var slot = Array.Find(playerSlots, s => s.PlayerId == player.Id);
        if (slot != null)
        {
            slot.SetPlayer(player);
        }
    }

    #endregion

    #region Utility

    /// <summary>
    /// Creates a short 4-letter code, but does NOT check if it's used.
    /// We handle uniqueness in GenerateUniqueGameCode().
    /// </summary>
    private string GenerateRandomCode()
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
        // If you want to generate & display a QR for your gameCode:
        // string url = $"https://your-game-url.com/join/{gameCode}";
        // ...
        // qrCodeImage.texture = ...
    }

    #endregion
}

[Serializable]
public class Player
{
    public string Id;
    public string Name;
    public int Score;
    public bool HasVoted;
    public bool startGame; // <--- new field

    public Player(string id, string name)
    {
        Id = id;
        Name = name;
        Score = 0;
        HasVoted = false;
        startGame = false;
    }
}

[Serializable]
public class Answer
{
    public string Id;
    public string PlayerId;
    public string Content;
    public int Votes;

    public Answer(string playerId, string content)
    {
        Id = Guid.NewGuid().ToString();
        PlayerId = playerId;
        Content = content;
        Votes = 0;
    }
}
