using UnityEngine;
using UnityEngine.Networking;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine.UI;
using System.Collections;
using System.Text;
using System;
using TMPro;

public class PlayerSlot : MonoBehaviour
{
    [SerializeField] private TMP_Text playerNameText;
    [SerializeField] private TMP_Text scoreText;
    [SerializeField] private Image statusIcon;
    [SerializeField] public string PlayerId;

    public void SetPlayer(Player player)
    {
        this.PlayerId = player.Id;
        this.playerNameText.text = player.Name;
        this.scoreText.text = $"{player.Score}pts";
    }

    public void SetAnswerStatus(bool hasAnswered)
    {
        this.statusIcon.color = hasAnswered ? Color.green : Color.red;
    }

    public void Clear()
    {
        this.playerNameText.text = "Waiting...";
        this.scoreText.text = "";
        this.statusIcon.color = Color.gray;
    }
}
