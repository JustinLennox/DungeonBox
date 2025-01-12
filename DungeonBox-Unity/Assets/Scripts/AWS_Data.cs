using UnityEngine;
using UnityEngine.Networking;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Collections;
using System.Text;
using System;

[System.Serializable]
public class SendPostData
{
    public string message;
    public string sessionId;
}

[System.Serializable]
public class ResultPostData
{
	public int statusCode;
    public string body;
}

[System.Serializable]
public class BedrockResponse
{
    public string response;
	public string sessionId;
}