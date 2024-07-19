using Unity.Netcode;
using UnityEngine;
using System.Collections;
using Unity.Services.Core;
using Unity.Services.Authentication;
using Unity.Services.Relay;
using Unity.Services.Relay.Models;
using Unity.Netcode.Transports.UTP;
using Unity.Networking.Transport.Relay;
using Unity.Services.Lobbies;
using Unity.Services.Lobbies.Models;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

public class MenuManager : NetworkBehaviour
{
    [SerializeField]
    private Animator m_menuAnimator;

    [SerializeField]
    private CharacterDataSO[] m_characterDatas;

    [SerializeField]
    private AudioClip m_confirmClip;

    private bool m_pressAnyKeyActive = true;
    private const string k_enterMenuTriggerAnim = "enter_menu";

    [SerializeField]
    private SceneName nextScene = SceneName.CharacterSelection;

    private async Task Authenticate()
    {
        var options = new InitializationOptions();

#if UNITY_EDITOR
        // Remove this if you don't have ParrelSync installed. 
        // It's used to differentiate the clients, otherwise lobby will count them as the same
        options.SetProfile(ParrelSync.ClonesManager.IsClone() ? ParrelSync.ClonesManager.GetArgument() : "Primary");
#endif

        await UnityServices.InitializeAsync(options);

        await AuthenticationService.Instance.SignInAnonymouslyAsync();
        _playerId = AuthenticationService.Instance.PlayerId;
    }
    private async void OnEnable()
    {
        await Authenticate();
    }
    private IEnumerator Start()
    {
        DontDestroyOnLoad(gameObject);
        
        // -- To test with latency on development builds --
        // To set the latency, jitter and packet-loss percentage values for develop builds we need
        // the following code to execute before NetworkManager attempts to connect (changing the
        // values of the parameters as desired).
        //
        // If you'd like to test without the simulated latency, just set all parameters below to zero(0).
        //
        // More information here:
        // https://docs-multiplayer.unity3d.com/netcode/current/tutorials/testing/testing_with_artificial_conditions#debug-builds
#if DEVELOPMENT_BUILD && !UNITY_EDITOR
        NetworkManager.Singleton.GetComponent<Unity.Netcode.Transports.UTP.UnityTransport>().
            SetDebugSimulatorParameters(
                packetDelay: 50,
                packetJitter: 5,
                dropRate: 3);
#endif

        ClearAllCharacterData();

        // Wait for the network Scene Manager to start
        yield return new WaitUntil(() => NetworkManager.Singleton.SceneManager != null);

        // Set the events on the loading manager
        // Doing this because every time the network session ends the loading manager stops
        // detecting the events
        LoadingSceneManager.Instance.Init();
    }
    private bool wantsToQuit = false;
    private void Update()
    {
        if (m_pressAnyKeyActive)
        {
            if (Input.anyKey)
            {
                TriggerMainMenuTransitionAnimation();

                m_pressAnyKeyActive = false;
            }
        }
    }

    public async void OnClickHost()
    {
        const int maxPlayers = 3;

        try
        {
            Allocation allocation = await RelayService.Instance.CreateAllocationAsync(maxPlayers);

            string joinCode = await RelayService.Instance.GetJoinCodeAsync(allocation.AllocationId);

            // Create a lobby, adding the relay join code to the lobby data
            var options = new CreateLobbyOptions
            {
                Data = new Dictionary<string, DataObject> { { JoinCodeKey, new DataObject(DataObject.VisibilityOptions.Public, joinCode) } }
            };
            var lobby = await Lobbies.Instance.CreateLobbyAsync("Useless Lobby Name", maxPlayers, options);
            _connectedLobby = lobby;

            // Send a heartbeat every 15 seconds to keep the room alive
            StartCoroutine(HeartbeatLobbyCoroutine(lobby.Id, 15));

            GameObject.FindGameObjectWithTag("RoomCode").GetComponent<TMPro.TextMeshProUGUI>().text = joinCode;
            RelayServerData relayServerData = new RelayServerData(allocation, "dtls");
            _transport.SetRelayServerData(relayServerData); ;
            NetworkManager.Singleton.StartHost();
        }
        catch (RelayServiceException e)
        {
            Debug.Log(e);
        }
        AudioManager.Instance.PlaySoundEffect(m_confirmClip);
        LoadingSceneManager.Instance.LoadScene(nextScene);
    }

    public async void OnClickJoin()
    {
        _connectedLobby = await QuickJoinLobby() ?? await CreateLobby();

        AudioManager.Instance.PlaySoundEffect(m_confirmClip);
    }

    public void OnClickQuit()
    {
        AudioManager.Instance.PlaySoundEffect(m_confirmClip);
        Application.Quit();
    }

    private void ClearAllCharacterData()
    {
        // Clean the all the data of the characters so we can start with a clean slate
        foreach (CharacterDataSO data in m_characterDatas)
        {
            data.EmptyData();
        }
    }

    private void TriggerMainMenuTransitionAnimation()
    {
        m_menuAnimator.SetTrigger(k_enterMenuTriggerAnim);
        AudioManager.Instance.PlaySoundEffect(m_confirmClip);
    }

    // We use a coroutine because the server is the one who makes the load
    // we need to make a fade first before calling the start client
    private IEnumerator Join()
    {
        LoadingFadeEffect.Instance.FadeAll();

        yield return new WaitUntil(() => LoadingFadeEffect.s_canLoad);

        NetworkManager.Singleton.StartClient();
    }

    //___________________Lobby_____________________

    private Lobby _connectedLobby;
    private QueryResponse _lobbies;
    private UnityTransport _transport;
    private const string JoinCodeKey = "j";
    private string _playerId;

    private void Awake() => _transport = FindObjectOfType<UnityTransport>();


    private async Task<Lobby> QuickJoinLobby()
    {
        try
        {
            // Attempt to join a lobby in progress
            var lobby = await Lobbies.Instance.QuickJoinLobbyAsync();

            // If we found one, grab the relay allocation details
            var a = await RelayService.Instance.JoinAllocationAsync(lobby.Data[JoinCodeKey].Value);

            GameObject.FindGameObjectWithTag("RoomCode").GetComponent<TMPro.TextMeshProUGUI>().text = lobby.Data[JoinCodeKey].Value;
            Debug.Log($"Connected to: " + lobby.Data[JoinCodeKey].Value);

            // Set the details to the transform
            SetTransformAsClient(a);

            // Join the game room as a client
            StartCoroutine(Join());
            return lobby;
        }
        catch (Exception e)
        {
            Debug.Log($"No lobbies available via quick join");
            return null;
        }
    }

    private async Task<Lobby> CreateLobby()
    {
        try
        {
            const int maxPlayers = 3;

            // Create a relay allocation and generate a join code to share with the lobby
            var a = await RelayService.Instance.CreateAllocationAsync(maxPlayers);
            var joinCode = await RelayService.Instance.GetJoinCodeAsync(a.AllocationId);

            // Create a lobby, adding the relay join code to the lobby data
            var options = new CreateLobbyOptions
            {
                Data = new Dictionary<string, DataObject> { { JoinCodeKey, new DataObject(DataObject.VisibilityOptions.Public, joinCode) } }
            };
            var lobby = await Lobbies.Instance.CreateLobbyAsync("Useless Lobby Name", maxPlayers, options);

            // Send a heartbeat every 15 seconds to keep the room alive
            StartCoroutine(HeartbeatLobbyCoroutine(lobby.Id, 15));

            // Set the game room to use the relay allocation
            RelayServerData relayServerData = new RelayServerData(a, "dtls");
            _transport.SetRelayServerData(relayServerData); ;

            // Start the room. I'm doing this immediately, but maybe you want to wait for the lobby to fill up
            NetworkManager.Singleton.StartHost();
            GameObject.FindGameObjectWithTag("RoomCode").GetComponent<TMPro.TextMeshProUGUI>().text = joinCode;
            LoadingSceneManager.Instance.LoadScene(nextScene);
            return lobby;
        }
        catch (Exception e)
        {
            Debug.LogFormat("Failed creating a lobby");
            return null;
        }
    }

    private void SetTransformAsClient(JoinAllocation a)
    {
        RelayServerData relayServerData = new RelayServerData(a, "dtls");
        _transport.SetRelayServerData(relayServerData);
    }

    private static IEnumerator HeartbeatLobbyCoroutine(string lobbyId, float waitTimeSeconds)
    {
        var delay = new WaitForSecondsRealtime(waitTimeSeconds);
        while (true)
        {
            Lobbies.Instance.SendHeartbeatPingAsync(lobbyId);
            yield return delay;
        }
    }

    private void OnDisable()
    {
        try
        {
            StopAllCoroutines();
            // todo: Add a check to see if you're host
            if (_connectedLobby != null)
            {
                if (_connectedLobby.HostId == _playerId)
                {
                    Lobbies.Instance.DeleteLobbyAsync(_connectedLobby.Id);
                    GoBackToMainClientRpc();
                    Debug.Log($"Deleted Lobby");
                }
                else
                {
                    Lobbies.Instance.RemovePlayerAsync(_connectedLobby.Id, _playerId);
                    Debug.Log($"Removed Player");
                }
            }
        }
        catch (Exception e)
        {
            Debug.Log($"Error shutting down lobby: {e}");
        }
    }

    private GameObject MM;
    [ClientRpc]
    private void GoBackToMainClientRpc()
    {
        if (IsServer)
        {
            return;
        }

        MM = GameObject.FindWithTag("MenuManager");
        Destroy(MM);

        NetworkManager.Singleton.Shutdown();
        LoadingSceneManager.Instance.LoadScene(SceneName.Menu, false);
    }
}