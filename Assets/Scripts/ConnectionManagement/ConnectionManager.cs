using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using VContainer;

namespace Unity.Multiplayer.Samples.BossRoom
{
    public enum ConnectStatus
    {
        Undefined,
        Success,                  //client successfully connected. This may also be a successful reconnect.
        ServerFull,               //can't join, server is already at capacity.
        LoggedInAgain,            //logged in on a separate client, causing this one to be kicked out.
        UserRequestedDisconnect,  //Intentional Disconnect triggered by the user.
        GenericDisconnect,        //server disconnected, but no specific reason given.
        Reconnecting,             //client lost connection and is attempting to reconnect.
        IncompatibleBuildType,    //client build type is incompatible with server.
        HostEndedSession,         //host intentionally ended the session.
        StartHostFailed,          // server failed to bind
        StartClientFailed         // failed to connect to server and/or invalid network endpoint
    }

    public struct ReconnectMessage
    {
        public int CurrentAttempt;
        public int MaxAttempt;

        public ReconnectMessage(int currentAttempt, int maxAttempt)
        {
            CurrentAttempt = currentAttempt;
            MaxAttempt = maxAttempt;
        }
    }

    public struct ConnectionEventMessage : INetworkSerializeByMemcpy
    {
        public ConnectStatus ConnectStatus;
        public FixedPlayerName PlayerName;
    }

    [Serializable]
    public class ConnectionPayload
    {
        public string playerId;
        public string playerName;
        public bool isDebug;
    }

    /// <summary>
    /// This state machine handles connection through the NetworkManager. It is responsible for listening to
    /// NetworkManger callbacks and other outside calls and redirecting them to the current ConnectionState object.
    /// </summary>
    public class ConnectionManager : MonoBehaviour
    {
        ConnectionState m_CurrentState;

        [SerializeField]
        NetworkManager m_NetworkManager;
        public NetworkManager NetworkManager => m_NetworkManager;

        [SerializeField]
        NetworkObject m_GameState;
        public NetworkObject GameState => m_GameState;

        [Inject]
        IObjectResolver m_Resolver;

        public int MaxConnectedPlayers = 8;

        internal readonly OfflineState m_Offline = new OfflineState();
        internal readonly ClientConnectingState m_ClientConnecting = new ClientConnectingState();
        internal readonly ClientConnectedState m_ClientConnected = new ClientConnectedState();
        internal readonly ClientReconnectingState m_ClientReconnecting = new ClientReconnectingState();
        internal readonly DisconnectingWithReasonState m_DisconnectingWithReason = new DisconnectingWithReasonState();
        internal readonly ServerStartingState m_ServerStarting = new ServerStartingState();
        internal readonly ServerListeningState m_ServerListening = new ServerListeningState();
        internal readonly StartingHostState m_StartingHost = new StartingHostState();
        internal readonly HostingState m_Hosting = new HostingState();

        public bool IsConnectedToHost { get; private set; }

        void Awake()
        {
            DontDestroyOnLoad(gameObject);
        }

        void Start()
        {
            List<ConnectionState> states = new() { m_Offline, m_ClientConnecting, m_ClientConnected, m_ClientReconnecting, m_DisconnectingWithReason, m_StartingHost, m_Hosting };
            foreach (var connectionState in states)
            {
                m_Resolver.Inject(connectionState);
            }

            m_CurrentState = m_Offline;

            NetworkManager.OnClientConnectedCallback += OnClientConnectedCallback;
            NetworkManager.OnClientDisconnectCallback += OnClientDisconnectCallback;
            NetworkManager.OnServerStarted += OnServerStarted;
            NetworkManager.ConnectionApprovalCallback += ApprovalCheck;
        }

        void OnDestroy()
        {
            NetworkManager.OnClientConnectedCallback -= OnClientConnectedCallback;
            NetworkManager.OnClientDisconnectCallback -= OnClientDisconnectCallback;
            NetworkManager.OnServerStarted -= OnServerStarted;
            NetworkManager.ConnectionApprovalCallback -= ApprovalCheck;
        }

        internal void ChangeState(ConnectionState nextState)
        {
            Debug.Log($"Changed connection state from {m_CurrentState.GetType().Name} to {nextState.GetType().Name}.");

            IsConnectedToHost = false;

            if (m_CurrentState != null)
            {
                m_CurrentState.Exit();
            }

            m_CurrentState = nextState;
            m_CurrentState.Enter();
        }

        void OnClientDisconnectCallback(ulong clientId)
        {
            m_CurrentState.OnClientDisconnect(clientId);
        }

        void OnClientConnectedCallback(ulong clientId)
        {
            m_CurrentState.OnClientConnected(clientId);
        }

        void OnServerStarted()
        {
            m_CurrentState.OnServerStarted();
        }

        void ApprovalCheck(NetworkManager.ConnectionApprovalRequest request, NetworkManager.ConnectionApprovalResponse response)
        {
            m_CurrentState.ApprovalCheck(request, response);
        }

        public void StartClientLobby(string playerName)
        {
            m_CurrentState.StartClientLobby(playerName);
        }

        public void StartClientIp(string playerName, string ipaddress, int port)
        {
            m_CurrentState.StartClientIP(playerName, ipaddress, port);
        }

        public void StartHostLobby(string playerName)
        {
            m_CurrentState.StartHostLobby(playerName);
        }

        public void StartHostIp(string playerName, string ipaddress, int port)
        {
            m_CurrentState.StartHostIP(playerName, ipaddress, port);
        }

        public void StartServerIP(string ipaddress, int port)
        {
            m_CurrentState.StartServerIP(ipaddress, port);
        }

        public void RequestShutdown()
        {
            m_CurrentState.OnUserRequestedShutdown();
        }

        /// <summary>
        /// Registers the message handler for custom named messages. This should only be done once StartClient has been
        /// called (start client will initialize NetworkSceneManager and CustomMessagingManager)
        /// This will override the message handler each time and will use this instance's method.
        /// </summary>
        public void RegisterCustomMessages()
        {
            NetworkManager.CustomMessagingManager.RegisterNamedMessageHandler(nameof(ReceiveServerToClientSetDisconnectReason_CustomMessage), ReceiveServerToClientSetDisconnectReason_CustomMessage);
            NetworkManager.CustomMessagingManager.RegisterNamedMessageHandler(nameof(ReceiveServertoClientSuccessPayload_CustomMessage), ReceiveServertoClientSuccessPayload_CustomMessage);
        }

        void ReceiveServerToClientSetDisconnectReason_CustomMessage(ulong clientID, FastBufferReader reader)
        {
            reader.ReadValueSafe(out ConnectStatus status);
            m_CurrentState.OnDisconnectReasonReceived(status);
        }

        /// <summary>
        /// Sends a DisconnectReason to all connected clients. This should only be done on the server, prior to disconnecting the clients.
        /// </summary>
        /// <param name="status"> The reason for the upcoming disconnect.</param>
        public static void SendServerToAllClientsSetDisconnectReason(ConnectStatus status)
        {
            var writer = new FastBufferWriter(sizeof(ConnectStatus), Allocator.Temp);
            writer.WriteValueSafe(status);
            NetworkManager.Singleton.CustomMessagingManager.SendNamedMessageToAll(nameof(ReceiveServerToClientSetDisconnectReason_CustomMessage), writer);
        }

        /// <summary>
        /// Sends a DisconnectReason to the indicated client. This should only be done on the server, prior to disconnecting the client.
        /// </summary>
        /// <param name="clientID"> id of the client to send to </param>
        /// <param name="status"> The reason for the upcoming disconnect.</param>
        public static void SendServerToClientSetDisconnectReason(ulong clientID, ConnectStatus status)
        {
            var writer = new FastBufferWriter(sizeof(ConnectStatus), Allocator.Temp);
            writer.WriteValueSafe(status);
            NetworkManager.Singleton.CustomMessagingManager.SendNamedMessage(nameof(ReceiveServerToClientSetDisconnectReason_CustomMessage), clientID, writer);
        }

        public void ReceiveServertoClientSuccessPayload_CustomMessage(ulong clientID, FastBufferReader reader)
        {
            reader.ReadValueSafe(out bool isHost);
            IsConnectedToHost = isHost;
        }

        public static void SendServertoClientSuccessPayload(ulong clientID, bool isHost)
        {
            var writer = new FastBufferWriter(sizeof(bool), Allocator.Temp);
            writer.WriteValueSafe(isHost);
            NetworkManager.Singleton.CustomMessagingManager.SendNamedMessage(nameof(ReceiveServertoClientSuccessPayload_CustomMessage), clientID, writer);
        }
    }
}