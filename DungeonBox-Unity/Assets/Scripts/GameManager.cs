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
    [SerializeField] private GameObject promptContainer;
    [SerializeField] private TMP_Text promptText;
    [SerializeField] private TMP_Text timerText;
    [SerializeField] private Image qrCodeImage;
    [SerializeField] private GridLayoutGroup playerGrid;

    // New UI references
    [SerializeField] private GridLayoutGroup answersGrid;  // Grid for listing all answers
    [SerializeField] private AnswerSlot topVoteSlot;         // Text for showing top voted answer

    // We'll discover PlayerSlots at runtime
    private List<PlayerSlot> playerSlots = new List<PlayerSlot>();

    // We'll discover AnswerSlots at runtime
    private List<AnswerSlot> answerSlots = new List<AnswerSlot>();

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
        // Find all PlayerSlot children in playerGrid at runtime
        playerSlots = playerGrid.GetComponentsInChildren<PlayerSlot>(true).ToList();

        // Find all AnswerSlot children in answersGrid at runtime
        answerSlots = answersGrid.GetComponentsInChildren<AnswerSlot>(true).ToList();

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

    private async System.Threading.Tasks.Task<string> GenerateUniqueGameCode()
    {
        while (true)
        {
            string candidate = GenerateRandomCode();
            var snapshot = await dbRootRef.Child("games").Child(candidate).GetValueAsync();
            if (!snapshot.Exists)
            {
                return candidate;
            }
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

        // We do NOT parse "gameData.state" because Unity is the authority on state.

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
                    string playerName = answerDict.ContainsKey("playerName")
                        ? answerDict["playerName"].ToString()
                        : "";
                    int votes = 0;
                    if (answerDict.ContainsKey("votes"))
                    {
                        int.TryParse(answerDict["votes"].ToString(), out votes);
                    }

                    var newAnswer = new Answer(playerId, content)
                    {
                        Id = answerId,
                        Votes = votes,
                        PlayerName = playerName
                    };
                    currentAnswers.Add(newAnswer);
                }
            }
        }

        // If we're in the Voting state, update answersGrid UI
        if (currentState == GameState.Voting)
        {
            UpdateAnswersUI();
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
        promptContainer.gameObject.SetActive(false);
        timerText.gameObject.SetActive(false);
        qrCodeImage.gameObject.SetActive(false);
        playerGrid.gameObject.SetActive(false);
        answersGrid.gameObject.SetActive(false);
        topVoteSlot.gameObject.SetActive(false);

        UpdatePlayerSlots();
    }

    private void SetGameState(GameState newState)
    {
        if (currentState == newState) return;
        currentState = newState;
        Debug.Log($"Game state changed to: {newState}");

        answersGrid.gameObject.SetActive(false);
        topVoteSlot.gameObject.SetActive(false);

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
                promptContainer.gameObject.SetActive(false);
                timerText.gameObject.SetActive(false);
                break;

            case GameState.SubmittingAnswers:
                ClearOldAnswersAndResetVotes();

                promptContainer.gameObject.SetActive(true);
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
                // Show answersGrid so we can display the answers
                answersGrid.gameObject.SetActive(true);

                timerText.gameObject.SetActive(true);
                playerGrid.gameObject.SetActive(true);

                titleLogo.SetActive(false);
                playButton.SetActive(false);
                promptContainer.gameObject.SetActive(false);
                promptText.gameObject.SetActive(false);
                gameCodeText.gameObject.SetActive(false);
                qrCodeImage.gameObject.SetActive(false);

                StartCoroutine(VotingPhase());
                // Update once now that we have set the state
                UpdateAnswersUI();
                break;

            case GameState.FinishedVoting:
                promptContainer.gameObject.SetActive(false);
                promptText.gameObject.SetActive(false);
                titleLogo.SetActive(false);
                playButton.SetActive(false);
                gameCodeText.gameObject.SetActive(false);
                qrCodeImage.gameObject.SetActive(false);
                timerText.gameObject.SetActive(false);
                playerGrid.gameObject.SetActive(true);

                // Show the final result (top vote)
                topVoteSlot.gameObject.SetActive(true);

                StartCoroutine(FinishedVotingPhase());
                break;
        }

        UpdateFirebaseState(newState);
    }

    private async void ClearOldAnswersAndResetVotes()
    {
        currentAnswers.Clear();
        await dbRootRef.Child("games").Child(gameCode).Child("answers").RemoveValueAsync();

        var playerList = players.ToList();
        foreach (var kvp in playerList)
        {
            kvp.Value.HasVoted = false;
            var pid = kvp.Key;
            await dbRootRef.Child("games").Child(gameCode).Child("players").Child(pid).Child("hasVoted").SetValueAsync(false);
        }
    }

    private IEnumerator SubmittingAnswersPhase()
    {
        timer = 60f;
        while (timer > 0 && !AllPlayersSubmitted())
        {
            timer -= Time.deltaTime;
            timerText.text = $"{Mathf.CeilToInt(timer)}";
            yield return null;
        }
        SetGameState(GameState.Voting);
    }

    private bool AllPlayersSubmitted()
    {
        return currentAnswers.Count == players.Count;
    }

    private IEnumerator VotingPhase()
    {
        timer = 30f;
        while (timer > 0 && !AllPlayersVoted())
        {
            timer -= Time.deltaTime;
            timerText.text = $"{Mathf.CeilToInt(timer)}";
            yield return null;
        }
        SetGameState(GameState.FinishedVoting);
    }

    private IEnumerator FinishedVotingPhase()
    {
        var topAnswer = GetTopVotedAnswer();
        if (topAnswer != null)
        {
            // Show the top answer
            topVoteSlot.SetAnswer(topAnswer);
            UpdatePlayerScore(topAnswer.PlayerId);
            awsInteractor.SendMessageToServer(topAnswer.Content);
        }
        else
        {
            // No answers?
            topVoteSlot.Clear();
        }

        yield return new WaitForSeconds(10f);
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
        // Convert dictionary to a list
        var playerArray = players.Values.ToList();

        for (int i = 0; i < playerSlots.Count; i++)
        {
            if (i < playerArray.Count)
            {
                playerSlots[i].SetPlayer(playerArray[i]);
                playerSlots[i].gameObject.SetActive(true);
            }
            else
            {
                playerSlots[i].Clear();
                playerSlots[i].gameObject.SetActive(false);
            }
        }
    }

    /// <summary>
    /// Fills the answersGrid children with currentAnswers data in Voting state.
    /// Hides extra slots if we have fewer answers than UI children.
    /// </summary>
    private void UpdateAnswersUI()
    {
        // Convert list so we can index them
        var answerArray = currentAnswers.ToList();

        for (int i = 0; i < answerSlots.Count; i++)
        {
            if (i < answerArray.Count)
            {
                answerSlots[i].SetAnswer(answerArray[i]);
                answerSlots[i].gameObject.SetActive(true);
            }
            else
            {
                answerSlots[i].Clear();
                answerSlots[i].gameObject.SetActive(false);
            }
        }
    }

    private void UpdatePlayerUI(Player player)
    {
        // If you want to just re-draw that single slot
        var slot = playerSlots.FirstOrDefault(s => s.PlayerId == player.Id);
        if (slot != null)
        {
            slot.SetPlayer(player);
        }
    }

    #endregion

    #region Utility

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
        // e.g. string url = $"https://your-game-url.com/join/{gameCode}";
        // assign to qrCodeImage if needed
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
    public string PlayerName;
    public int Votes;

    public Answer(string playerId, string content)
    {
        Id = Guid.NewGuid().ToString();
        PlayerId = playerId;
        Content = content;
        Votes = 0;
    }
}
