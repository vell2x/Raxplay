using UnityEngine;

public class AudioManager : MonoBehaviour
{
    public GameObject player;
    public bool isPlaying = false;
    // Start is called before the first frame update
    void Start()
    {

    }

    // Update is called once per frame
    void Update()
    {

    }
    public void affirmative()
    {
        player.GetComponent<AudioSource>().Play();
        isPlaying = false;
    }

    public void negative()
    {
        player.GetComponent<AudioSource>().Pause();
        isPlaying = true;
    }

    public void toggleAudio()
    {
        if (isPlaying)
        {
            player.GetComponent<AudioSource>().Play();
            isPlaying = false;
        }
        else
        {
            player.GetComponent<AudioSource>().Pause();
            isPlaying = true;
        }
    }
}
