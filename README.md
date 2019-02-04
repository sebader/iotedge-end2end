# Azure IoT Edge - End2End testing loop

This solution is an end-to-end solution to test a full Azure IoT Edge setup. It consists of two parts:
1) Two Azure Functions that run in the cloud
2) Two custom modules running on the Edge

The testing loop is started by a timer-triggered (every couple of minutes) Azure Function (Direct Method Caller), which attempts to execute a direct method on a module running in IoT Edge (Direct Method Receiver).
This module creates a new IoT messages and sends it to the Edge Hub. The message gets routed to the next module (Message Forwarder). This one forwards the message back to the Edge Hub where another route sends the message to the IoT Hub back in Azure ("$upstream").
In the cloud, a second Function is triggered by new telemetry messages on the IoT Hub. This concludes the complete loop.

All steps in the cycle are getting logged into Application Insights. This enables reporting and alerting if there are any errors at any point in the loop - and also about the end to end duration of the message from the start in the cloud until it is being received again in the cloud.

![architecture](https://raw.githubusercontent.com/sebader/iotedge-end2end/master/Media/architecture_diagram.png)
