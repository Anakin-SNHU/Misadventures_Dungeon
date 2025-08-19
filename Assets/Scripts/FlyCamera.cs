using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class FlyCamera : MonoBehaviour
{
    public float moveSpeed = 5f;
    public float fastSpeed = 15f;
    public float sensitivity = 2f;

    private float currentSpeed;

    void Update()
    {
        // Hold right mouse to look around
        if (Input.GetMouseButton(1))
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;

            float rotX = Input.GetAxis("Mouse X") * sensitivity;
            float rotY = -Input.GetAxis("Mouse Y") * sensitivity;
            transform.eulerAngles += new Vector3(rotY, rotX, 0f);
        }
        else
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        currentSpeed = Input.GetKey(KeyCode.LeftShift) ? fastSpeed : moveSpeed;

        Vector3 direction = Vector3.zero;
        if (Input.GetKey(KeyCode.W)) direction += transform.forward;
        if (Input.GetKey(KeyCode.S)) direction -= transform.forward;
        if (Input.GetKey(KeyCode.A)) direction -= transform.right;
        if (Input.GetKey(KeyCode.D)) direction += transform.right;
        if (Input.GetKey(KeyCode.E)) direction += transform.up;
        if (Input.GetKey(KeyCode.Q)) direction -= transform.up;

        transform.position += direction * currentSpeed * Time.deltaTime;
    }
}

