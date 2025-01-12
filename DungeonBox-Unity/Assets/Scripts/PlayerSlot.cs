using UnityEngine;
using UnityEngine.Networking;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine.UI;
using System.Collections;
using System.Text;
using System;

public class PlayerSlot : MonoBehaviour
{
    [SerializeField] private Text playerNameText;
    [SerializeField] private Text scoreText;
    [SerializeField] private Image statusIcon;

    public void SetPlayer(Player player)
    {
        playerNameText.text = player.Name;
        scoreText.text = $"Score: {player.Score}";
    }

    public void SetAnswerStatus(bool hasAnswered)
    {
        statusIcon.color = hasAnswered ? Color.green : Color.red;
    }

    public void Clear()
    {
        playerNameText.text = "Waiting...";
        scoreText.text = "";
        statusIcon.color = Color.gray;
    }
}
