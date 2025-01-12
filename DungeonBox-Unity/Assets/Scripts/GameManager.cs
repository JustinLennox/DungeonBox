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
                // Unity decides the initial state, ignoring what’s in DB
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
        playButton.SetActive(true);
    }

    public void OnPlayButtonClicked()
    {
        // Reset sessionId to empty and send "Start Game" to AI
        awsInteractor.sessionId = "";
        awsInteractor.SendMessageToServer("Start Game");

        CreateGameSession();
    }

    private async void CreateGameSession()
    {
        try
        {
            gameCode = GenerateGameCode();
            gameCodeText.text = gameCode;

            // Just store some minimal data for the new game
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

            // Unity alone sets the state:
            SetGameState(GameState.Lobby);
            GenerateQRCode();

            // Start one real-time listener for players/answers, ignoring "state"
            SetupRealtimeListeners(gameCode);
        }
        catch (Exception e)
        {
            Debug.LogError($"Failed to create game session: {e.Message}");
        }
    }

    private void SetupRealtimeListeners(string code)
    {
        // We'll watch /games/{code} for changes in players or answers
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

        // We do NOT parse "gameData.state" at all here.

        // --- Parse players
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

                    // **New**: check for "startGame"
                    bool startGame = false;
                    if (playerDict.ContainsKey("startGame"))
                    {
                        bool.TryParse(playerDict["startGame"].ToString(), out startGame);
                    }

                    // Create or update the local Player object
                    Player p = new Player(playerId, playerName)
                    {
                        Score = score,
                        HasVoted = hasVoted,
                        startGame = startGame
                    };

                    players[playerId] = p;
                }

                UpdatePlayerSlots();

                // Now that we have up-to-date player data:
                // If we are in LOBBY, check if any player wants to "startGame"
                if (currentState == GameState.Lobby)
                {
                    bool anyStartGame = players.Values.Any(pl => pl.startGame == true);
                    if (anyStartGame)
                    {
                        // We'll transition the game to SubmittingAnswers
                        // and also set everyone’s startGame back to false
                        Debug.Log("Player requested startGame => moving to SubmittingAnswers");
                        SetGameState(GameState.SubmittingAnswers);
                        ClearStartGameFlags();
                    }
                }
            }
        }

        // --- Parse answers
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
        // Make a snapshot of the current dictionary
        var playerList = players.ToList(); // each item is KeyValuePair<string, Player>

        // Now iterate over the snapshot instead of the live 'players' dictionary
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
                promptText.text = awsInteractor.currentPrompt;
                promptText.gameObject.SetActive(true);
                timerText.gameObject.SetActive(true);
                playerGrid.gameObject.SetActive(true);

                titleLogo.SetActive(false);
                playButton.SetActive(false);
                gameCodeText.gameObject.SetActive(false);
                qrCodeImage.gameObject.SetActive(false);

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

        // We DO still push the new state to Firebase so the web can see it
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
            awsInteractor.SendMessageToServer(topAnswer.Content);
        }

        yield return new WaitForSeconds(10f);

        currentAnswers.Clear();
        dbRootRef.Child("games").Child(gameCode).Child("answers").RemoveValueAsync();

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
        // The Unity host remains the "authority."
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
        // If you want to generate & display a QR for your gameCode:
        // string url = $"https://your-game-url.com/join/{gameCode}";
        // ...
        // qrCodeImage.texture = ...
    }

    #endregion
}

// -----------------------------------------
// Updated Player model with `startGame`
// -----------------------------------------
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
