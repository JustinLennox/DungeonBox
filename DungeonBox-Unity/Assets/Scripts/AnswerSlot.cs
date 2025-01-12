using UnityEngine;
using UnityEngine.Networking;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine.UI;
using System.Collections;
using System.Text;
using System;
using TMPro;

public class AnswerSlot : MonoBehaviour
{
    [SerializeField] public TMP_Text answerText;
    [SerializeField] public TMP_Text playerNameText;
    [SerializeField] public TMP_Text votesText;
    [SerializeField] public string PlayerId;

	public void SetAnswer(Answer answer)
    {
        this.answerText.text = answer.Content;
        this.playerNameText.text = answer.PlayerName;
        this.votesText.text = answer.Votes.ToString();
    }

    public void Clear()
    {
        this.answerText.text = "";
        this.votesText.text = "";
        this.playerNameText.text = "";
    }
}
