import React, { useEffect, useState } from 'react';
import {
  SafeAreaView,
  View,
  Text,
  TextInput,
  Button,
  FlatList,
  StyleSheet
} from 'react-native';
import AsyncStorage from '@react-native-async-storage/async-storage';

// Firebase (Modular v9)
import {
  initializeApp
} from 'firebase/app';
import {
  getDatabase,
  ref,
  onValue,
  update,
  get,
  set,
  child
} from 'firebase/database';

// Your Firebase config
const firebaseConfig = {
  apiKey: "AIzaSyANB8KWneTX1lPTmDa8tva_gZMUSi-7whc",
  authDomain: "dungeonbox-6b962.firebaseapp.com",
  databaseURL: "https://dungeonbox-6b962-default-rtdb.firebaseio.com",
  projectId: "dungeonbox-6b962",
  storageBucket: "dungeonbox-6b962.firebasestorage.app",
  messagingSenderId: "133037359197",
  appId: "1:133037359197:web:60d96cc6097b77e6330db4"
};
const app = initializeApp(firebaseConfig);
const db = getDatabase(app);

// Match the Unity GameState enum
enum GameState {
  PreGame = 'PreGame',
  Lobby = 'Lobby',
  SubmittingAnswers = 'SubmittingAnswers',
  Voting = 'Voting',
  FinishedVoting = 'FinishedVoting',
  GameOver = 'GameOver'
}

export default function App() {
  // **Separate** ephemeral typed text from the joined room code
  const [tempRoomCode, setTempRoomCode] = useState<string>('');
  const [tempPlayerName, setTempPlayerName] = useState<string>('');

  // The actual joined room code (stored in local storage)
  const [roomCode, setRoomCode] = useState<string>('');
  const [currentGameState, setCurrentGameState] = useState<GameState>(GameState.PreGame);

  // Player info
  const [playerId, setPlayerId] = useState<string>('');
  const [answerText, setAnswerText] = useState<string>('');
  const [answers, setAnswers] = useState<any[]>([]);

  // --------------------------------------------
  // On mount, generate a random player ID and check local storage
  // --------------------------------------------
  useEffect(() => {
    (async () => {
      const randomId = 'player_' + Math.floor(Math.random() * 1000000);
      setPlayerId(randomId);
      await checkLocalRoomCode();
    })();
  }, []);

  // --------------------------------------------
  // Whenever `roomCode` changes, set up a listener on that room
  // --------------------------------------------
  useEffect(() => {
    if (!roomCode) {
      // If no room code, no listener => remain in PreGame
      setCurrentGameState(GameState.PreGame);
      return;
    }

    const roomRef = ref(db, `games/${roomCode}`);
    const unsubscribe = onValue(roomRef, (snapshot) => {
      if (!snapshot.exists()) {
        // Room removed or doesn't exist
        console.log('Room not found; clearing local storage...');
        clearLocalRoom();
        return;
      }
      const data = snapshot.val();

      // Sync game state
      if (data.state) {
        if (data.state === GameState.GameOver) {
          console.log('Game is over, clearing local room...');
          clearLocalRoom();
          return;
        } else {
          setCurrentGameState(data.state);
        }
      }

      // Gather answers if present
      if (data.answers) {
        const arr: any[] = Object.keys(data.answers).map((key) => ({
          id: key,
          ...data.answers[key],
        }));
        setAnswers(arr);
      } else {
        setAnswers([]);
      }
    });

    return () => unsubscribe();
  }, [roomCode]);

  // --------------------------------------------
  // AsyncStorage Helpers
  // --------------------------------------------
  const checkLocalRoomCode = async () => {
    try {
      const storedCode = await AsyncStorage.getItem('roomCode');
      if (!storedCode) {
        return; // no code => remain in PreGame
      }
      // Check if the stored room is valid + not GameOver
      const snap = await get(child(ref(db), `games/${storedCode}`));
      if (!snap.exists()) {
        await clearLocalRoom();
        return;
      }
      const data = snap.val();
      if (data.state === GameState.GameOver) {
        await clearLocalRoom();
        return;
      }
      // If valid
      setRoomCode(storedCode);
    } catch (error) {
      console.log('Error checking local room:', error);
    }
  };

  const clearLocalRoom = async () => {
    try {
      await AsyncStorage.removeItem('roomCode');
      setRoomCode('');
      setCurrentGameState(GameState.PreGame);
    } catch (error) {
      console.log('Error clearing local room:', error);
    }
  };

  // --------------------------------------------
  // Join Room
  // --------------------------------------------
  const joinRoom = async () => {
    if (!tempRoomCode || !tempPlayerName) {
      alert('Please enter both a room code and your name.');
      return;
    }

    try {
      // Check if room exists
      const snap = await get(child(ref(db), `games/${tempRoomCode}`));
      if (!snap.exists()) {
        alert('That room does not exist!');
        return;
      }

      const data = snap.val();
      if (data.state === GameState.GameOver) {
        alert('This game has ended!');
        return;
      }

      // If valid, store the code in local storage
      await AsyncStorage.setItem('roomCode', tempRoomCode);
      setRoomCode(tempRoomCode);

      // Write this player into the players list
      const playerData = {
        playerId: playerId,
        playerName: tempPlayerName,
        score: 0,
        hasVoted: false,
      };
      await update(ref(db, `games/${tempRoomCode}/players/${playerId}`), playerData);

      console.log('Joined room:', tempRoomCode);
    } catch (error) {
      console.log('Error joining room:', error);
    }
  };

  // --------------------------------------------
  // Start Game (Lobby -> SubmittingAnswers)
  // --------------------------------------------
  const startGame = async () => {
    if (!roomCode) return;
    try {
      await update(ref(db, `games/${roomCode}/players/${playerId}`), { startGame: true });
    } catch (error) {
      console.log('Error starting game:', error);
    }
  };

  // --------------------------------------------
  // Submitting Answers
  // --------------------------------------------
  const submitAnswer = async () => {
    if (!answerText.trim()) {
      alert('Please enter an answer');
      return;
    }
    // Store under /answers
    const newKey = playerId + '_' + Date.now();
    const answerData = {
      playerId,
      content: answerText.trim(),
      votes: 0,
    };
    await set(ref(db, `games/${roomCode}/answers/${newKey}`), answerData);
    setAnswerText('');
  };

  // --------------------------------------------
  // Voting on an Answer
  // --------------------------------------------
  const voteOnAnswer = async (answerId: string) => {
    try {
      const votesRef = ref(db, `games/${roomCode}/answers/${answerId}/votes`);
      const snap = await get(votesRef);
      const currentVotes = snap.exists() ? snap.val() : 0;
      await set(votesRef, currentVotes + 1);

      // Mark player as hasVoted if your logic requires it
      const playerRef = ref(db, `games/${roomCode}/players/${playerId}`);
      await update(playerRef, { hasVoted: true });
    } catch (error) {
      console.log('Error voting:', error);
    }
  };

  // --------------------------------------------
  // Render Logic
  // --------------------------------------------

  // If we have NO joined room, or the game is over => Show PreGame screen
  if (!roomCode || currentGameState === GameState.PreGame) {
    return (
      <SafeAreaView style={styles.container}>
        <Text style={styles.title}>Welcome to Dungeon Box!</Text>

        <Text>Enter Room Code:</Text>
        <TextInput
          style={styles.input}
          onChangeText={setTempRoomCode}
          value={tempRoomCode}
          placeholder="Room Code"
        />

        <Text>Enter Your Name:</Text>
        <TextInput
          style={styles.input}
          onChangeText={setTempPlayerName}
          value={tempPlayerName}
          placeholder="Your Name"
        />

        <Button title="Join" onPress={joinRoom} />
      </SafeAreaView>
    );
  }

  // If we do have a joined room:
  return (
    <SafeAreaView style={styles.container}>
      <Text style={styles.title}>Room: {roomCode}</Text>
      <Text>State: {currentGameState}</Text>

      {currentGameState === GameState.Lobby && (
        <View>
          <Text>Waiting in the lobby...</Text>
          {/* "Start Game" button here! */}
          <Button title="Start Game" onPress={startGame} />
        </View>
      )}

      {currentGameState === GameState.SubmittingAnswers && (
        <View>
          <Text>Submit Your Answer:</Text>
          <TextInput
            style={styles.answerInput}
            maxLength={300}
            value={answerText}
            onChangeText={setAnswerText}
            placeholder="Type your answer..."
          />
          <Button title="Submit" onPress={submitAnswer} />
        </View>
      )}

      {currentGameState === GameState.Voting && (
        <View style={{ flex: 1 }}>
          <Text>Vote on your favorite answer!</Text>
          <FlatList
            data={answers}
            keyExtractor={(item) => item.id}
            renderItem={({ item }) => (
              <View style={styles.answerItem}>
                <Text>{item.content}</Text>
                <Button
                  title="Vote"
                  onPress={() => voteOnAnswer(item.id)}
                />
              </View>
            )}
          />
        </View>
      )}

      {currentGameState === GameState.FinishedVoting && (
        <View>
          <Text>Finished Voting! Please wait for the next round...</Text>
        </View>
      )}

      {currentGameState === GameState.GameOver && (
        <View>
          <Text>Game Over!</Text>
          {/* Potentially show final scores */}
        </View>
      )}
    </SafeAreaView>
  );
}

// Some minimal styling
const styles = StyleSheet.create({
  container: {
    flex: 1,
    padding: 16,
    backgroundColor: '#fff',
  },
  title: {
    fontSize: 22,
    fontWeight: 'bold',
    marginBottom: 12,
  },
  input: {
    borderWidth: 1,
    borderColor: '#ccc',
    padding: 8,
    marginVertical: 8,
  },
  answerInput: {
    borderWidth: 1,
    borderColor: '#555',
    padding: 8,
    marginVertical: 8,
  },
  answerItem: {
    padding: 8,
    marginVertical: 4,
    borderWidth: 1,
    borderColor: '#ddd',
  },
});
