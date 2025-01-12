using UnityEngine;
using UnityEngine.UI;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

// Firebase imports
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
    [Header("UI References")]
    [SerializeField] private GameObject mainMenuPanel;
    [SerializeField] private GameObject lobbyPanel;
    [SerializeField] private GameObject gameplayPanel;
    [SerializeField] private GameObject votingPanel;
    [SerializeField] private GameObject resultsPanel;
    [SerializeField] private TMP_Text gameCodeText;
    [SerializeField] private TMP_Text promptText;
    [SerializeField] private TMP_Text timerText;
    [SerializeField] private RawImage qrCodeImage;
    [SerializeField] private GridLayoutGroup playerGrid;
    [SerializeField] private PlayerSlot[] playerSlots;

    // Reference to AWSInteractor for AI messaging
    private AWSInteractor awsInteractor;

    private GameState currentState = GameState.PreGame;
    private string gameCode;
    private float timer;
    private Dictionary<string, Player> players = new Dictionary<string, Player>();
    private List<Answer> currentAnswers = new List<Answer>();

    // Firebase references
    private DatabaseReference dbRootRef;

    private void Start()
    {
        // Get AWSInteractor on this same GameObject (or adjust as needed)
        awsInteractor = GetComponent<AWSInteractor>();
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

    private void InitializeFirebase()
    {
        dbRootRef = FirebaseDatabase.DefaultInstance.RootReference;
    }

    public void OnPlayButtonClicked()
    {
        // Reset sessionId to empty
        if (awsInteractor != null)
        {
            awsInteractor.sessionId = "";
            awsInteractor.SendMessageToServer("Start Game");
        }

        CreateGameSession();
    }

    /// <summary>
    /// Creates a new game session in Firebase, initializes a code, sets lobby state.
    /// </summary>
    private async void CreateGameSession()
    {
        try
        {
            gameCode = GenerateGameCode();
            gameCodeText.text = gameCode;

            // Create a game node in Firebase Realtime Database
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

            SetGameState(GameState.Lobby);
            GenerateQRCode();

            // Start one real-time listener for the entire game object
            SetupRealtimeListeners(gameCode);
        }
        catch (Exception e)
        {
            Debug.LogError($"Failed to create game session: {e.Message}");
        }
    }

    /// <summary>
    /// Sets up a single listener for the entire game data at /games/{gameCode}.
    /// We can parse state, players, and answers from this snapshot.
    /// </summary>
    private void SetupRealtimeListeners(string code)
    {
        FirebaseDatabase.DefaultInstance
            .GetReference("games")
            .Child(code)
            .ValueChanged += HandleGameDataChanged;
    }

    /// <summary>
    /// This method is triggered any time the /games/{gameCode} data changes.
    /// We parse the entire game object, updating local state, players, and answers.
    /// </summary>
    private void HandleGameDataChanged(object sender, ValueChangedEventArgs args)
    {
        if (args.Snapshot == null || args.Snapshot.Value == null) return;

        var gameData = args.Snapshot.Value as Dictionary<string, object>;
        if (gameData == null) return;

        // --- Parse the state ---
        if (gameData.ContainsKey("state"))
        {
            string newStateString = gameData["state"].ToString();
            if (Enum.TryParse(newStateString, out GameState newGameState))
            {
                // Let the host manage state transitions
                SetGameState(newGameState);
            }
        }

        // --- Parse players ---
        if (gameData.ContainsKey("players"))
        {
            var playersData = gameData["players"] as Dictionary<string, object>;
            if (playersData != null)
            {
                players.Clear();

                foreach (var kvp in playersData)
                {
                    // kvp.Key is typically the player's ID
                    var playerDict = kvp.Value as Dictionary<string, object>;
                    if (playerDict == null) continue;

                    string playerId = playerDict.ContainsKey("playerId") 
                        ? playerDict["playerId"].ToString() 
                        : kvp.Key;

                    string playerName = playerDict.ContainsKey("playerName") 
                        ? playerDict["playerName"].ToString() 
                        : "Unknown";

                    // Parse score
                    int score = 0;
                    if (playerDict.ContainsKey("score"))
                    {
                        int.TryParse(playerDict["score"].ToString(), out score);
                    }

                    // Parse hasVoted
                    bool hasVoted = false;
                    if (playerDict.ContainsKey("hasVoted"))
                    {
                        bool.TryParse(playerDict["hasVoted"].ToString(), out hasVoted);
                    }

                    players[playerId] = new Player(playerId, playerName)
                    {
                        Score = score,
                        HasVoted = hasVoted
                    };
                }

                UpdatePlayerSlots();
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

    #region Game State / Rounds

    private void SetGameState(GameState newState)
    {
        // If the state didn't actually change, do nothing
        if (currentState == newState) return;

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

        // Host updates Firebase "state" field so all clients see the new state
        UpdateFirebaseState(newState);
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
        if (topAnswer != null)
        {
            UpdatePlayerScore(topAnswer.PlayerId);

            // Send the top voted answer to AI
            if (awsInteractor != null)
            {
                awsInteractor.SendMessageToServer("Top voted answer: " + topAnswer.Content);
            }
        }

        yield return new WaitForSeconds(10f);

        // Clear old answers for the next round
        currentAnswers.Clear();
        // Also clear them in Firebase
        dbRootRef.Child("games").Child(gameCode).Child("answers").RemoveValueAsync();

        // Move to the next round
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

            // Also push score update to Firebase
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
                var player = playerArray[i];
                playerSlots[i].SetPlayer(player);
            }
            else
            {
                playerSlots[i].Clear();
            }
        }
    }

    private void UpdatePlayerUI(Player player)
    {
        // If you store a reference to Player in PlayerSlot,
        // be sure it matches how your PlayerSlot is set up
        var slot = Array.Find(playerSlots, s => s.PlayerId == player.Id);
        if (slot != null)
        {
            slot.SetPlayer(player);
        }
    }

    #endregion

    #region Utility

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
        // Example only, implement your own QR code generation if desired
        // string url = $"https://your-game-url.com/join/{gameCode}";
        // Set the generated texture to qrCodeImage
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

    public Player(string id, string name)
    {
        Id = id;
        Name = name;
        Score = 0;
        HasVoted = false;
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
