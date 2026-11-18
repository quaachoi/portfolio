using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using Unity.Netcode;

public class PlayerInputManager : NetworkBehaviour
{
    //Used for storing inputs received by clients
    Dictionary<int, PlayerInputData> pendingInputs = new();

    //Used by clients to replay inputs
    [SerializeField] CircularBuffer<PlayerInputData> circularBuffer = new CircularBuffer<PlayerInputData>();

    [SerializeField] InputActionAsset inputActionAsset;
    [SerializeField] Transform playerCameraOrientation;

    // [SerializeField] InputActionMap inputMap;
    List<InputAction> inputActions = new List<InputAction>();
    List<InputAction> directionalInputActions = new List<InputAction>();

    Dictionary<byte, InputAction> idToAction = new();
    Dictionary<InputAction, byte> actionToId = new();
    Dictionary<byte, InputAction> idToDirectionalAction = new();
    Dictionary<InputAction, byte> directionalActionToId = new();

    // [SerializeField] PlayerInputData mostRecentInputData; //Finalized input at the end of a tick
    [SerializeField] PlayerInputData tempInputData; //Used for collecting input mid tick
    
    [SerializeField] int evaluationOffset = 0; //to read an input in the past, this will change what input we read

    public void setEvaluationOffset(int value)
    {
        evaluationOffset = value;
    }

    public void removeEvaluationOffset()
    {
        evaluationOffset = 0;
    }

    PlayerInputData getInput()
    {
        if(IsServer)
        {
            int desiredTime = NetworkManager.Singleton.NetworkTickSystem.ServerTime.Tick;
            if(pendingInputs.Remove(desiredTime, out PlayerInputData data))
            {
                circularBuffer.Add(data);
            }

            //clean old inputs
            List<int> stale = null;
            foreach(int k in pendingInputs.Keys)
            {
                if(k < desiredTime) 
                {
                    (stale ??= new()).Add(k);
                }
            }
            if(stale != null) 
            {
                foreach(int k in stale) 
                {
                    pendingInputs.Remove(k);
                }
            }
        }

        return circularBuffer.GetHeadOffset(evaluationOffset);
    }

    public int getEvaluationOffset()
    {
        return evaluationOffset;
    }

    public int getMostRecentTick()
    {
        return circularBuffer.GetHead().timeStamp;
    }

    public Vector2 getLookRotation()
    {
        return getInput().getLookRotation();
    }

    public Vector2 getDirection(string name)
    {
        InputAction action = inputActionAsset.FindAction(name);
        if(action != null && !directionalActionToId.ContainsKey(action))
        {
            Debug.LogError($"No such directional input named {name}");
            return Vector2.zero;
        }

        if(getInput().directionalInputData == null)
        {
            return Vector2.zero;
        }

        return getInput().directionalInputData[directionalActionToId[action]].direction;
    }

    public bool isPressed(string name)
    {
        InputAction action = inputActionAsset.FindAction(name);
        if(action != null && !actionToId.ContainsKey(action))
        {
            Debug.LogError($"No such input named {name}");
            return false;
        }

        if(getInput().inputData == null)
        {
            return false;
        }

        return getInput().inputData[actionToId[action]].isPressed;
    }

    public bool isTriggered(string name)
    {
        InputAction action = inputActionAsset.FindAction(name);
        if(action != null && !actionToId.ContainsKey(action))
        {
            Debug.LogError($"No such input named {name}");
            return false;
        }

        if(getInput().inputData == null)
        {
            return false;
        }

        return getInput().inputData[actionToId[action]].isTriggered;
    }
    
    void Awake()
    {
        //Get all input actions in first action map
        foreach(InputAction i in inputActionAsset.actionMaps[0])
        {
            if(i.expectedControlType == "Vector2")
            {
                directionalInputActions.Add(i);
            }
            else
            {
                inputActions.Add(i);
            }
        }

        inputActions.Sort((a, b) => string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase)); //Deterministic sorting
        directionalInputActions.Sort((a, b) => string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase)); //Deterministic sorting

        //populate mostRecentInputData and tempInputData
        tempInputData.initialize(inputActions, directionalInputActions);

        for(byte i = 0; i < inputActions.Count; i++)
        {
            idToAction[i] = inputActions[i];
            actionToId[inputActions[i]] = i;
        }

        for(byte i = 0; i < directionalInputActions.Count; i++)
        {
            idToDirectionalAction[i] = directionalInputActions[i];
            directionalActionToId[directionalInputActions[i]] = i;
        }
    }

    void Update()
    {
        if(IsOwner)
        {
            setInputs();
        }
    }

    void setInputs()
    {
        tempInputData.lookRotation = new Vector2(playerCameraOrientation.eulerAngles.x,playerCameraOrientation.eulerAngles.y);
        for(int i = 0; i < inputActions.Count; i++)
        {
            if(inputActions[i].IsPressed())
            {
                tempInputData.inputData[i].isPressed = true;
            }

            if(inputActions[i].triggered)
            {
                tempInputData.inputData[i].isTriggered = true;
            }
        }
        for(int i = 0; i < directionalInputActions.Count; i++)
        {
            tempInputData.directionalInputData[i].direction = directionalInputActions[i].ReadValue<Vector2>();

            if(inputActions[i].IsPressed())
            {
                tempInputData.directionalInputData[i].isPressed = true;
            }

            if(inputActions[i].triggered)
            {
                tempInputData.directionalInputData[i].isTriggered = true;
            }
        }
    }
    public void finalizeInput()
    {
        if(!IsOwner)
        {
            return;
        }

        PlayerInputData newData = new PlayerInputData();
        newData.initialize(inputActions, directionalInputActions);
        newData.lookRotation = tempInputData.lookRotation;

        for(int i = 0; i < tempInputData.inputData.Length; i++)
        {
            newData.inputData[i].isPressed = tempInputData.inputData[i].isPressed;
            newData.inputData[i].isTriggered = tempInputData.inputData[i].isTriggered;
        }
        for(int i = 0; i < tempInputData.directionalInputData.Length; i++)
        {
            newData.directionalInputData[i].direction = tempInputData.directionalInputData[i].direction;
            newData.directionalInputData[i].isPressed = tempInputData.directionalInputData[i].isPressed;
            newData.directionalInputData[i].isTriggered = tempInputData.directionalInputData[i].isTriggered;
        }
        tempInputData.reset();
        circularBuffer.Add(newData);
        sendFinalInputServerRpc(newData);
    }

    [ServerRpc(Delivery = RpcDelivery.Unreliable)]
    void sendFinalInputServerRpc(PlayerInputData finalData)
    {
        if(IsOwner)
        {
            return;
        }
        if(circularBuffer.GetHead().timeStamp > finalData.timeStamp)
        {
            Debug.Log("server rejected input: old");
            return;
        }
        pendingInputs[finalData.timeStamp] = finalData; //Query input for selection
    }

    [System.Serializable]
    public struct PlayerInputData : INetworkSerializable
    {
        public int timeStamp;

        public Vector2 lookRotation;

        public InputActionData[] inputData; //Server refers to this to replay input on the server.
        public DirectionalInputActionData[] directionalInputData;

        public void reset()
        {
            lookRotation = Vector2.zero;
            for(int i = 0; i < inputData.Length; i++)
            {
                inputData[i].isPressed = false;
                inputData[i].isTriggered = false;
            }
            for(int i = 0; i < directionalInputData.Length; i++)
            {
                directionalInputData[i].direction = Vector2.zero;
                directionalInputData[i].isPressed = false;
                directionalInputData[i].isTriggered = false;
            }
        }

        public void initialize(List<InputAction> inputList, List<InputAction> directionalInputList)
        {
            timeStamp = NetworkManager.Singleton.NetworkTickSystem.LocalTime.Tick;
            inputData = new InputActionData[inputList.Count];
            directionalInputData = new DirectionalInputActionData[directionalInputList.Count];
        }

        public Vector2 getLookRotation()
        {
            return lookRotation;
        }

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref timeStamp);

            int length = inputData != null ? inputData.Length : 0;
            int dirLength = directionalInputData != null ? directionalInputData.Length : 0;
            serializer.SerializeValue(ref length);
            serializer.SerializeValue(ref dirLength);

            serializer.SerializeValue(ref lookRotation);

            if (serializer.IsReader)
            {
                inputData = new InputActionData[length];
                directionalInputData = new DirectionalInputActionData[dirLength];
            }

            for(int i = 0; i < length; i++)
            {
                serializer.SerializeValue(ref inputData[i]);
            }
            for(int i = 0; i < dirLength; i++)
            {
                serializer.SerializeValue(ref directionalInputData[i]);
            }
        }
    }

    [System.Serializable]
    public struct InputActionData : INetworkSerializable
    {
        public bool isPressed;
        public bool isTriggered;

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref isPressed);
            serializer.SerializeValue(ref isTriggered);
        }
    }

    [System.Serializable]
    public struct DirectionalInputActionData : INetworkSerializable
    {
        public Vector2 direction;
        public bool isPressed;
        public bool isTriggered;

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref direction);
            serializer.SerializeValue(ref isPressed);
            serializer.SerializeValue(ref isTriggered);
        }
    }
}
