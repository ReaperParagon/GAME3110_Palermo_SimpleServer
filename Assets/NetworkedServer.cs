using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using System.IO;
using UnityEngine.UI;

public class NetworkedServer : MonoBehaviour
{
    int maxConnections = 1000;
    int reliableChannelID;
    int unreliableChannelID;
    int hostID;
    int socketPort = 5491;

    string playerAccountsFilepath;

    int playerWaitingForMatchWithID = -1;

    LinkedList<PlayerAccount> playerAccounts;
    List<GameRoom> gameRooms;
    List<string> currentPlayers;
    ReplaySystem replaySystem;

    // Start is called before the first frame update
    void Start()
    {
        NetworkTransport.Init();
        ConnectionConfig config = new ConnectionConfig();
        reliableChannelID = config.AddChannel(QosType.Reliable);
        unreliableChannelID = config.AddChannel(QosType.Unreliable);
        HostTopology topology = new HostTopology(config, maxConnections);
        hostID = NetworkTransport.AddHost(topology, socketPort, null);

        playerAccounts = new LinkedList<PlayerAccount>();
        gameRooms = new List<GameRoom>();
        currentPlayers = new List<string>();
        playerAccountsFilepath = Application.dataPath + Path.DirectorySeparatorChar + "Accounts.txt";

        // Load all the replay file information
        replaySystem = new ReplaySystem();
        replaySystem.LoadReplays();

        // Read in player accounts
        LoadPlayerAccounts();
    }

    // Update is called once per frame
    void Update()
    {

        int recHostID;
        int recConnectionID;
        int recChannelID;
        byte[] recBuffer = new byte[1024];
        int bufferSize = 1024;
        int dataSize;
        byte error = 0;

        NetworkEventType recNetworkEvent = NetworkTransport.Receive(out recHostID, out recConnectionID, out recChannelID, recBuffer, bufferSize, out dataSize, out error);

        switch (recNetworkEvent)
        {
            case NetworkEventType.Nothing:
                break;
            case NetworkEventType.ConnectEvent:
                Debug.Log("Connection, " + recConnectionID);
                break;
            case NetworkEventType.DataEvent:
                string msg = Encoding.Unicode.GetString(recBuffer, 0, dataSize);
                ProcessRecievedMsg(msg, recConnectionID);
                break;
            case NetworkEventType.DisconnectEvent:
                Debug.Log("Disconnection, " + recConnectionID);

                // Remove Player from game room, if they were in one
                ProcessRecievedMsg(ClientToServerSignifiers.LeaveRoom + "", recConnectionID);
                break;
        }

    }
  
    public void SendMessageToClient(string msg, int id)
    {
        byte error = 0;
        byte[] buffer = Encoding.Unicode.GetBytes(msg);
        NetworkTransport.Send(hostID, id, reliableChannelID, buffer, msg.Length * sizeof(char), out error);
    }
    
    private void ProcessRecievedMsg(string msg, int id)
    {
        Debug.Log("msg recieved = " + msg + ".  connection id = " + id);

        string[] csv = msg.Split(',');

        int signifier = int.Parse(csv[0]);

        if (signifier == ClientToServerSignifiers.CreateAccount)
        {
            // Check if player account name already exists, 
            string n = csv[1];
            string p = csv[2];
            bool nameInUse = false;

            foreach (PlayerAccount pa in playerAccounts)
            {
                if (pa.name == n)
                {
                    nameInUse = true;
                    break;
                }
            }
            
            if (nameInUse)
            {
                SendMessageToClient(ServerToClientSignifiers.AccountCreationFailed + ",: Name already in use", id); 
            }
            else
            {
                PlayerAccount newPlayerAccount = new PlayerAccount(n, p);
                playerAccounts.AddLast(newPlayerAccount);
                SendMessageToClient(ServerToClientSignifiers.AccountCreationComplete + ",: Succesful Account Creation", id);

                // Add player to current player list
                AddPlayerToCurrentPlayers(id, n);

                // Save to list HD
                SavePlayerAccounts();
            }
        }
        else

        if (signifier == ClientToServerSignifiers.Login)
        {
            // Check if player account name already exists, 
            PlayerAccount loginPlayer = null;
            string n = csv[1];
            string p = csv[2];
            bool nameInUse = false;

            foreach (PlayerAccount pa in playerAccounts)
            {
                if (pa.name == n)
                {
                    loginPlayer = pa;
                    nameInUse = true;
                    break;
                }
            }

            if (nameInUse)
            {
                // Check password, if correct
                if (p == loginPlayer.password)
                {
                    SendMessageToClient(ServerToClientSignifiers.LoginComplete + ",: Successful Login", id);

                    // Add player to current player list
                    AddPlayerToCurrentPlayers(id, n);
                }
                else
                {
                    // Password is not correct
                    SendMessageToClient(ServerToClientSignifiers.LoginFailed + ",: Wrong Password", id);
                }

            }
            else
            {
                // Login does not exist
                SendMessageToClient(ServerToClientSignifiers.LoginFailed + ",: No Account exists", id);
            }
        }
        else 

        if (signifier == ClientToServerSignifiers.JoinQueueForGameRoom)
        {
            Debug.Log("Get the Player into a waiting queue!");

            if (playerWaitingForMatchWithID == -1)
            {
                playerWaitingForMatchWithID = id;
            }
            else
            {
                GameRoom gr = GetAvailableGameRoom(playerWaitingForMatchWithID, id);
                gr.ResetBoard();

                // 0 plays first, 1 plays second
                SendMessageToClient(ServerToClientSignifiers.GameStart + "," + TeamSignifier.O, playerWaitingForMatchWithID);
                SendMessageToClient(ServerToClientSignifiers.GameStart + "," + TeamSignifier.X, id);

                // Send information to observers as well
                foreach (int observer in gr.observerIDs)
                {
                    SendMessageToClient(ServerToClientSignifiers.GameStart + "," + TeamSignifier.None, observer);
                }

                playerWaitingForMatchWithID = -1;
            }

        }
        else

        if (signifier == ClientToServerSignifiers.TicTacToePlay)
        {
            // Get game room for client ID
            GameRoom gr = GetGameRoomWithClientID(id);

            // If game room exists
            if (gr != null)
            {
                int location = int.Parse(csv[1]);

                // Setup the team that played and get opponent ID
                int team;
                int opponentID;

                if (gr.playerID1 == id)
                {
                    team = TeamSignifier.O;
                    opponentID = gr.playerID2;
                }
                else
                {
                    team = TeamSignifier.X;
                    opponentID = gr.playerID1;
                }

                // Play the indicated move
                PlayAMove(gr, team, location, opponentID);
            }
        }
        else

        if (signifier == ClientToServerSignifiers.LeaveRoom)
        {
            // Remove ID from game rooms
            GameRoom gr = GetGameRoomWithClientID(id);

            if (gr != null)
            {
                // Check if they were an observer
                bool wasObserver = false;

                foreach (int observer in gr.observerIDs)
                {
                    if (observer == id)
                    {
                        wasObserver = true;
                        break;
                    }
                }

                // Check if they were still playing and not an observer, if so: award the other player a win
                if (!wasObserver && gr.gameInProgress)
                {
                    Debug.Log("Game was in progress... Awarding a win");

                    // Since Player 1 is Os and Player 2 is Xs, we can send the proper win information

                    int winner = WinStates.Tie;

                    if (gr.playerID1 == id)
                    {
                        SendMessageToClient(ServerToClientSignifiers.GameOver + "," + WinStates.XsWin, gr.playerID2);
                        winner = WinStates.XsWin;
                    }
                    else if (gr.playerID2 == id)
                    {
                        SendMessageToClient(ServerToClientSignifiers.GameOver + "," + WinStates.OsWin, gr.playerID1);
                        winner = WinStates.OsWin;
                    }

                    foreach (int observer in gr.observerIDs)
                    {
                        SendMessageToClient(ServerToClientSignifiers.GameOver + "," + winner, observer);
                    }

                    // Game has now concluded
                    gr.gameInProgress = false;

                    // Remove in between delimiter that separates moves if there was replay info available
                    if (gr.replayInfo.Length > 0)
                        gr.replayInfo = gr.replayInfo.Substring(0, gr.replayInfo.Length - 1);
                }

                Debug.Log("Removing Player from Game Room");

                // Remove ID from room
                gr.RemoveMatchingID(id);
            }
        }
        else 

        if (signifier == ClientToServerSignifiers.TextMessage)
        {
            // Add player Name to Message
            string name = GetPlayerAccountFromCurrentID(id);

            string message = name + ": " + csv[1];

            // Find the room the player is in
            GameRoom gr = GetGameRoomWithClientID(id);

            // Send message to all participants
            if (gr != null)
            {
                SendMessageToClient(ServerToClientSignifiers.TextMessage + "," + message, gr.playerID1);
                SendMessageToClient(ServerToClientSignifiers.TextMessage + "," + message, gr.playerID2);

                foreach (int observer in gr.observerIDs)
                {
                    SendMessageToClient(ServerToClientSignifiers.TextMessage + "," + message, observer);
                }
            }
            
        }
        else

        if (signifier == ClientToServerSignifiers.RequestReplayList)
        {
            // Get indices of all the replays from the named player
            string playerName = GetPlayerAccountFromCurrentID(id);
            List<int> replayIndices = replaySystem.GetAllReplayIndicesWithNamedPlayer(playerName);

            // Tell Player to reset replays
            SendMessageToClient(ServerToClientSignifiers.ReplayIndexList + "," + ReplayTransferSignifiers.ResetIndexList, id);

            // Send player indices with their games
            foreach (int ri in replayIndices)
            {
                SendMessageToClient(ServerToClientSignifiers.ReplayIndexList + "," + ReplayTransferSignifiers.IndexNumber + "," + ri, id);
            }
        }
        else

        if (signifier == ClientToServerSignifiers.RequestReplayByIndex)
        {
            int indexToLoad = int.Parse(csv[1]);

            // Get the replay information into a list
            List<string> replayStepList = replaySystem.GetReplayStepListFromIndex(indexToLoad);

            // Tell player that we are starting to send replay information
            SendMessageToClient(ServerToClientSignifiers.ReplayInformation + "," + ReplayTransferSignifiers.StartReplayStep, id);

            // Send each step of the replay
            foreach (string rs in replayStepList)
            {
                SendMessageToClient(ServerToClientSignifiers.ReplayInformation + "," + ReplayTransferSignifiers.ReplayStep + "," + rs, id);
            }

            // Tell player that we have finished sending replay information
            SendMessageToClient(ServerToClientSignifiers.ReplayInformation + "," + ReplayTransferSignifiers.EndReplaySteps, id);
        }
        else

        if (signifier == ClientToServerSignifiers.GetGameRoomList)
        {
            // Send the server list to the player
            foreach (GameRoom room in gameRooms)
            {
                int roomID = room.roomID;
                SendMessageToClient(ServerToClientSignifiers.GameRoomList + "," + roomID + "," + room.observerIDs.Count, id);
            }
        }
        else

        if (signifier == ClientToServerSignifiers.SpectateGame)
        {
            int roomID = int.Parse(csv[1]);

            // Add the player as an observer in the specified game room
            gameRooms[roomID].observerIDs.Add(id);
            SendMessageToClient(ServerToClientSignifiers.GameStart + "," + TeamSignifier.None, id);
        }
    }

    private void PlayAMove(GameRoom gr, int team, int location, int opponentID)
    {
        // Record info in game room
        gr.gameBoard[location] = team;
        gr.replayInfo += location + "," + team;

        // Check for a win
        if (gr.CheckWin())
        {
            // Get the win state depending on the team that won
            int teamWinState;

            if (team == TeamSignifier.O)
                teamWinState = WinStates.OsWin;
            else
                teamWinState = WinStates.XsWin;

            // Tell opponent and observers about the win
            SendInfoToOpponentAndObservers(gr, opponentID, ServerToClientSignifiers.OpponentPlayed + "," + location + "," + team + "," + teamWinState);

            DeclareResult(gr, teamWinState);
        }
        else if (gr.CheckTie())
        {
            // Tell opponent and observers about the tie
            SendInfoToOpponentAndObservers(gr, opponentID, ServerToClientSignifiers.OpponentPlayed + "," + location + "," + team + "," + WinStates.Tie);

            DeclareResult(gr, WinStates.Tie);
        }
        else
        {
            // else, Continue playing
            gr.replayInfo += "\n";

            SendInfoToOpponentAndObservers(gr, opponentID, ServerToClientSignifiers.OpponentPlayed + "," + location + "," + team + "," + WinStates.ContinuePlay);
        }
    }

    private void SendInfoToOpponentAndObservers(GameRoom gr, int opponentID, string msg)
    {
        // Tell opponent
        SendMessageToClient(msg, opponentID);

        // Tell Observers
        foreach (int observer in gr.observerIDs)
        {
            SendMessageToClient(msg, observer);
        }
    }

    private void DeclareResult(GameRoom gr, int winState)
    {
        // Save replay to a file
        string player1 = GetPlayerAccountFromCurrentID(gr.playerID1);
        string player2 = GetPlayerAccountFromCurrentID(gr.playerID2);

        replaySystem.SaveReplay(gr.replayInfo, player1, player2);

        // Tell both players about the result
        SendMessageToClient(ServerToClientSignifiers.GameOver + "," + winState, gr.playerID1);
        SendMessageToClient(ServerToClientSignifiers.GameOver + "," + winState, gr.playerID2);

        // Tell Observers about the result
        foreach (int observer in gr.observerIDs)
        {
            SendMessageToClient(ServerToClientSignifiers.GameOver + "," + winState, observer);
        }
    }

    private void SavePlayerAccounts()
    {
        StreamWriter sw = new StreamWriter(playerAccountsFilepath);

        foreach (PlayerAccount pa in playerAccounts)
        {
            sw.WriteLine(pa.name + "," + pa.password);
        }

        sw.Close();
    }

    private void LoadPlayerAccounts()
    {
        if (File.Exists(playerAccountsFilepath))
        {
            StreamReader sr = new StreamReader(playerAccountsFilepath);

            string line;
            while ((line = sr.ReadLine()) != null)
            {
                Debug.Log(line);

                string[] csv = line.Split(',');

                PlayerAccount pa = new PlayerAccount(csv[0], csv[1]);

                playerAccounts.AddLast(pa);
            }

            sr.Close();
        }
    }

    private void AddPlayerToCurrentPlayers(int id, string name)
    {
        // Add slots to match player id
        while (id >= currentPlayers.Count)
        {
            // Add Empty player until at id's index position
            currentPlayers.Add("");
        }

        // Add player to id slot
        currentPlayers[id] = name;
    }

    private string GetPlayerAccountFromCurrentID(int id)
    {
        return currentPlayers[id];
    }

    private GameRoom GetGameRoomWithClientID(int id)
    {
        foreach(GameRoom gr in gameRooms)
        {
            // Checking for players
            if (gr.playerID1 == id || gr.playerID2 == id)
            {
                return gr;
            }

            // Checking for observers
            foreach (int observer in gr.observerIDs)
            {
                if (observer == id)
                    return gr;
            }
        }

        return null;
    }

    private GameRoom GetAvailableGameRoom(int ID1, int ID2)
    {
        GameRoom gr = null;

        foreach (GameRoom room in gameRooms)
        {
            if (room != null && room.CheckAvailable())
            {
                gr = room;
                break;
            }
        }

        // Check if we went through all game rooms, create a new one if not found
        if (gr == null)
        {
            gr = new GameRoom(gameRooms.Count, ID1, ID2);
            gameRooms.Add(gr);
        }

        // Setup room and return it
        gr.SetupRoom(ID1, ID2);
        return gr;
    }
}


public class PlayerAccount
{
    public string name, password;

    public PlayerAccount(string Name, string Password)
    {
        name = Name;
        password = Password;
    }
}

public class GameRoom
{
    public int roomID;

    public int playerID1, playerID2;

    public List<int> observerIDs = new List<int>();

    public int[] gameBoard = new int[9];

    public string replayInfo;   // . separates tile location and team, ; separates each move

    public bool gameInProgress = false;

    public GameRoom(int index, int PlayerID1, int PlayerID2)
    {
        roomID = index;
        playerID1 = PlayerID1;
        playerID2 = PlayerID2;

        // Setup initial board
        ResetBoard();
        gameInProgress = true;
    }

    public void SetupRoom(int PlayerID1, int PlayerID2)
    {
        // Check if available
        if (CheckAvailable())
        {
            // Setup Players and Board

            playerID1 = PlayerID1;
            playerID2 = PlayerID2;

            ResetBoard();
            gameInProgress = true;
        }
    }

    public bool CompareSlots(int slot1, int slot2, int slot3)
    {
        // If one of the slots is not empty
        if (slot1 != TeamSignifier.None)
        {
            // If the Slots are all the same
            if (slot1 == slot2 && slot2 == slot3)
            {
                // We have a winner!
                return true;
            }
        }

        // No win yet
        return false;
    }

    public bool CheckWin()
    {
        // Compare slots for all different combinations, if any are true, return true
        if (CompareSlots(gameBoard[Board.TopLeft], gameBoard[Board.TopMid], gameBoard[Board.TopRight])       ||
            CompareSlots(gameBoard[Board.MidLeft], gameBoard[Board.MidMid], gameBoard[Board.MidRight])       ||
            CompareSlots(gameBoard[Board.BotLeft], gameBoard[Board.BotMid], gameBoard[Board.BotRight])       ||
            CompareSlots(gameBoard[Board.TopLeft], gameBoard[Board.MidLeft], gameBoard[Board.BotLeft])       ||
            CompareSlots(gameBoard[Board.TopMid], gameBoard[Board.MidMid], gameBoard[Board.BotMid])          ||
            CompareSlots(gameBoard[Board.TopRight], gameBoard[Board.MidRight], gameBoard[Board.BotRight])    ||
            CompareSlots(gameBoard[Board.TopLeft], gameBoard[Board.MidMid], gameBoard[Board.BotRight])       ||
            CompareSlots(gameBoard[Board.TopRight], gameBoard[Board.MidMid], gameBoard[Board.BotLeft]))
        {
            gameInProgress = false;
            return true;
        }

        // No win found
        return false;
    }

    public bool CheckTie()
    {
        // If there is no winner...
        if (!CheckWin())
        {
            // ...And all the slots are not empty...
            foreach (int slot in gameBoard)
            {
                if (slot == TeamSignifier.None)
                    return false;
            }

            // ...Then we have a tie
            gameInProgress = false;
            return true;
        }

        // There is a winner in this sceneario, no tie
        return false;
    }

    public void RemoveMatchingID(int id)
    {
        // Remove matching ID
        if (playerID1 == id)
        {
            playerID1 = -1;
        }
        else if (playerID2 == id)
        {
            playerID2 = -1;
        }

        // Checking observers as well
        foreach (int observer in observerIDs)
        {
            if (observer == id)
            {
                observerIDs.Remove(id);
                break;
            }
        }
    }

    public bool CheckAvailable()
    {
        if (playerID1 == -1 && playerID2 == -1)
        {
            return true;
        }

        return false;
    }

    public void ResetBoard()
    {
        // Reset stored replay information
        replayInfo = "";

        // Reset stored board information
        for (int i = 0; i < gameBoard.Length; i++)
        {
            gameBoard[i] = TeamSignifier.None;
        }
    }
}

public class ReplaySystem
{

    const string IndexFilePath = "replayIndex.txt";
    public int lastIndexUsed;
    LinkedList<PlayersAndIndex> replayNamesAndIndices;

    public void LoadReplays()
    {
        replayNamesAndIndices = new LinkedList<PlayersAndIndex>();

        if (File.Exists(Application.dataPath + Path.DirectorySeparatorChar + IndexFilePath))
        {
            StreamReader sr = new StreamReader(Application.dataPath + Path.DirectorySeparatorChar + IndexFilePath);

            string line;
            while ((line = sr.ReadLine()) != null)
            {
                Debug.Log(line);

                string[] csv = line.Split(',');
                int signifier = int.Parse(csv[0]);

                if (signifier == ReplayReadSignifier.LastUsedIndexSignifier)
                {
                    lastIndexUsed = int.Parse(csv[1]);
                }
                else if (signifier == ReplayReadSignifier.IndexAndNameSignifier)
                {
                    replayNamesAndIndices.AddLast(new PlayersAndIndex(int.Parse(csv[1]), csv[2], csv[3]));
                }
            }

            sr.Close();
        }
    }

    public List<int> GetAllReplayIndicesWithNamedPlayer(string namedPlayer)
    {
        // Create list to send back
        List<int> indexList = new List<int>();

        foreach (PlayersAndIndex playersAndIndex in replayNamesAndIndices)
        {
            if (namedPlayer == playersAndIndex.player1 || namedPlayer == playersAndIndex.player2)
            {
                // Add that replay to the list
                indexList.Add(playersAndIndex.index);
            }
        }

        return indexList;
    }

    public List<string> GetReplayStepListFromIndex(int index)
    {
        string filePath = index + ".txt";
        List<string> replayStepList = new List<string>();

        if (File.Exists(Application.dataPath + Path.DirectorySeparatorChar + filePath))
        {
            StreamReader sr = new StreamReader(Application.dataPath + Path.DirectorySeparatorChar + filePath);

            string line;
            while ((line = sr.ReadLine()) != null)
            {
                replayStepList.Add(line);
            }

            sr.Close();
        }

        return replayStepList;
    }

    public void SaveReplay(string replayInfo, string player1, string player2)
    {
        // Get / Create a new name for the saved file
        lastIndexUsed++;
        replayNamesAndIndices.AddLast(new PlayersAndIndex(lastIndexUsed, player1, player2));

        // Save information to a local file
        StreamWriter sw = new StreamWriter(Application.dataPath + Path.DirectorySeparatorChar + lastIndexUsed + ".txt");

        sw.WriteLine(replayInfo);

        sw.Close();

        // Add file to a list of replay files
        SaveReplayToList();
    }

    public void SaveReplayToList()
    {
        StreamWriter sw = new StreamWriter(Application.dataPath + Path.DirectorySeparatorChar + IndexFilePath);

        sw.WriteLine(ReplayReadSignifier.LastUsedIndexSignifier + "," + lastIndexUsed);

        foreach (PlayersAndIndex playersAndIndex in replayNamesAndIndices)
        {
            sw.WriteLine(ReplayReadSignifier.IndexAndNameSignifier + "," + playersAndIndex.index + "," + playersAndIndex.player1 + "," + playersAndIndex.player2);
        }

        sw.Close();
    }

}

public class PlayersAndIndex
{
    public string player1;
    public string player2;
    public int index;

    public PlayersAndIndex(int Index, string p1, string p2)
    {
        index = Index;
        player1 = p1;
        player2 = p2;
    }
}

static public class ReplayReadSignifier
{
    public const int LastUsedIndexSignifier = 1;
    public const int IndexAndNameSignifier = 2;
    public const int ResetLocalReplayFiles = 100;
}

public static class Board
{
    public const int TopLeft = 0;
    public const int TopMid = 1;
    public const int TopRight = 2;
    public const int MidLeft = 3;
    public const int MidMid = 4;
    public const int MidRight = 5;
    public const int BotLeft = 6;
    public const int BotMid = 7;
    public const int BotRight = 8;
}

public static class TeamSignifier
{
    public const int None = -1;
    public const int O = 0;
    public const int X = 1;
}

public static class ClientToServerSignifiers
{
    public const int CreateAccount = 1;
    public const int Login = 2;

    public const int JoinQueueForGameRoom = 3;

    public const int TicTacToePlay = 4;

    public const int LeaveRoom = 5;

    public const int TextMessage = 6;

    public const int RequestReplayList = 7;
    public const int RequestReplayByIndex = 8;
    public const int GetGameRoomList = 9;
    public const int SpectateGame = 10;
}

public static class ServerToClientSignifiers
{
    public const int LoginComplete = 1;
    public const int LoginFailed = 2;
    public const int AccountCreationComplete = 3;
    public const int AccountCreationFailed = 4;

    public const int OpponentPlayed = 5;    // Location, Team, Gameover?
    public const int GameStart = 6;

    public const int GameOver = 7;

    public const int TextMessage = 8;

    public const int ReplayIndexList = 9;
    public const int ReplayInformation = 10;
    public const int GameRoomList = 11;   // serverID , number of observers
}

public static class WinStates
{
    public const int ContinuePlay = 100;
    public const int OsWin = 0;
    public const int XsWin = 1;
    public const int Tie = 2;
}

public static class ReplayTransferSignifiers
{
    public const int ResetIndexList = 1;
    public const int IndexNumber = 2;
    public const int StartReplayStep = 3;
    public const int ReplayStep = 4;
    public const int EndReplaySteps = 5;
}