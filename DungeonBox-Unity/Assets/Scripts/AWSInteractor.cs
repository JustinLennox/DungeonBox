using UnityEngine;
using UnityEngine.Networking;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Collections;
using System.Text;
using System;

public class AWSInteractor : MonoBehaviour {
    public string sessionId = "";

    public void SendMessageToServer(string message)
    {    
        StartCoroutine(PostData(message, this.sessionId));
    }

    IEnumerator PostData(string message, string sessionId)
    {
        SendPostData sendPostData = new SendPostData();
        sendPostData.message = message;
        sendPostData.sessionId = sessionId;
        byte[] bytePostData = Encoding.UTF8.GetBytes(JsonUtility.ToJson(sendPostData));
        UnityWebRequest request = UnityWebRequest.Put($"https://v8ebz6ojbh.execute-api.us-east-1.amazonaws.com/DungeonBox/text", bytePostData);
        request.method = "POST";
        request.SetRequestHeader("Content-Type", "application/json");

        yield return request.SendWebRequest();

        if (request.result == UnityWebRequest.Result.Success)
        {
            Debug.Log(request.downloadHandler.text);

            ResultPostData resultPostData = JsonUtility.FromJson<ResultPostData>(request.downloadHandler.text);
            BedrockResponse bedrockResponse = JsonUtility.FromJson<BedrockResponse>(resultPostData.body);
            Debug.Log("Response: " + bedrockResponse.response);
             Debug.Log("Session ID: " + bedrockResponse.sessionId);
        
             if (bedrockResponse.sessionId != null)
            {
                Debug.Log("Setting session id to: " + bedrockResponse.sessionId);
                this.sessionId = bedrockResponse.sessionId;
            }
        }
        else
        {
            Debug.Log(request.error);
        }
    }
}