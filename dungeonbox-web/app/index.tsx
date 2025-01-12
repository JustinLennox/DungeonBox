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
import { v4 as uuidv4 } from 'uuid';

// Firebase (Modular v9)
import { initializeApp } from 'firebase/app';
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
  // Ephemeral typed text from user
  const [tempRoomCode, setTempRoomCode] = useState('');
  const [tempPlayerName, setTempPlayerName] = useState('');

  // The actual joined room code (stored in local storage)
  const [roomCode, setRoomCode] = useState('');
  const [currentGameState, setCurrentGameState] = useState<GameState>(GameState.PreGame);

  // Player info
  const [playerId, setPlayerId] = useState('');     // Will be persisted in AsyncStorage
  const [answerText, setAnswerText] = useState('');
  const [answers, setAnswers] = useState<any[]>([]);

  // --------------------------------------------
  // On mount: get or create a consistent playerId,
  // then check local storage for existing room code
  // --------------------------------------------
  useEffect(() => {
    (async () => {
      // 1) Retrieve existing playerId if it exists
      const storedPlayerId = await AsyncStorage.getItem('playerId');
      if (storedPlayerId) {
        setPlayerId(storedPlayerId);
      } else {
        // Create a new one
        const newId = 'player_' + uuidv4();
        setPlayerId(newId);
        await AsyncStorage.setItem('playerId', newId);
      }

      // 2) Check if user was in a room
      await checkLocalRoomCode();
    })();
  }, []);

  // --------------------------------------------
  // Whenever `roomCode` changes, set up a listener
  // --------------------------------------------
  useEffect(() => {
    if (!roomCode) {
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
        // Not in a room => remain PreGame
        return;
      }
      // Check if the stored room is valid
      const snap = await get(child(ref(db), `games/${storedCode}`));
      if (!snap.exists()) {
        // Not valid
        await clearLocalRoom();
        return;
      }
      const data = snap.val();
      if (data.state === GameState.GameOver) {
        // Also not valid
        await clearLocalRoom();
        return;
      }
      // If valid, set local roomCode
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

      // Store the code in local storage
      await AsyncStorage.setItem('roomCode', tempRoomCode);
      setRoomCode(tempRoomCode);

      // Write this player into the players list
      const playerData = {
        playerId,            // from state
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
  // Start Game (sets startGame = true in player's record)
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
    const newKey = `${playerId}_${Date.now()}`;
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
      await update(ref(db, `games/${roomCode}/players/${playerId}`), { hasVoted: true });
    } catch (error) {
      console.log('Error voting:', error);
    }
  };

  // --------------------------------------------
  // UI
  // --------------------------------------------

  // A small menu at the top-right or so. We'll just show a "Leave Game" button.
  const renderMenu = () => {
    return (
      <View style={styles.menuContainer}>
        <Button title="Leave Game" onPress={clearLocalRoom} />
      </View>
    );
  };

  return (
    <SafeAreaView style={styles.container}>
      <View style={{ padding: 20 }}>
        {/* 1) Always show a small banner "DungeonBox" */}
        <Text style={styles.banner}>DungeonBox</Text>

        {/* If in a room, show the mini menu (leave, etc.) */}
        {!!roomCode && renderMenu()}

        {(!roomCode || currentGameState === GameState.PreGame) ? (
          // ----------------------------------------
          // PreGame / Not in a room
          // ----------------------------------------
          <View style={{ marginTop: 16 }}>
            <Text style={styles.header}>Join a Room</Text>

            <Text>Enter Room Code:</Text>
            <TextInput
              autoCapitalize={"characters"}
              style={styles.input}
              onChangeText={(e) => { setTempRoomCode(e.substring(0, 4).toUpperCase()) }}
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
          </View>
        ) : (
          // ----------------------------------------
          // In a room
          // ----------------------------------------
          <View style={{ flex: 1 }}>
            {/* 2) We do NOT display the room code now that we've joined */}
            <Text style={styles.header}>State: {currentGameState}</Text>

            {currentGameState === GameState.Lobby && (
              <View>
                <Text>Waiting in the lobby...</Text>
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
          </View>
        )}
      </View>
    </SafeAreaView>
  );
}

const styles = StyleSheet.create({
  container: {
    flex: 1,
    backgroundColor: 'white',
    padding: 20,
  },
  banner: {
    fontSize: 20,
    fontWeight: 'bold',
    alignSelf: 'center',
    marginBottom: 8,
  },
  menuContainer: {
    alignSelf: 'flex-end',
    marginBottom: 8,
  },
  header: {
    fontSize: 18,
    fontWeight: '600',
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
