There are two UWP projects. One that runs on the hexapod, and the other that runs on a remote PC. Sonar (Maxbotix), Razor IMU and video is streamed to the remote PC using a MQTT broker. The broker used for testing can be found here: https://github.com/Slimordium/RxMqtt

There is a nuget package available for the MQTT Client here: https://www.nuget.org/packages?q=RxMQTT

The client, shared libraries and broker are .NET Standard 2.0. Currently compiled for x64, but should also work compiled for ARM.


