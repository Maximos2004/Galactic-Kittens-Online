using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using TMPro;
using Unity.Netcode;

public class TestRelay : NetworkBehaviour
{
    public TextMeshProUGUI textMeshPro;

    private void Awake()
    {
        DontDestroyOnLoad(gameObject);
    }

    private void Update()
    {
        if (!IsCharacterSelectionScene())
        {
            SetTextAlpha(0f);
        }
        else
        {
            SetTextAlpha(1f);
        }
    }

    private bool IsCharacterSelectionScene()
    {
        return UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == "CharacterSelection";
    }

    private void SetTextAlpha(float alphaValue)
    {
        Color textColor = textMeshPro.color;
        textColor.a = alphaValue;
        textMeshPro.color = textColor;
    }
}
