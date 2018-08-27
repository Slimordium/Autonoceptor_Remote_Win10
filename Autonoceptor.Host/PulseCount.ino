﻿

#define interruptPin 2 // pulse Counter input

volatile int SendCount = 0;

volatile long PulseCount = 0;

volatile float CmTraveled = 0;

volatile int Ms = 0;

volatile int Pulse10HzCount = 0;

void setup() 
{ 
  pinMode(interruptPin, INPUT_PULLUP);
  
  // put your setup code here, to run once:
  attachInterrupt(digitalPinToInterrupt(interruptPin), [](){ SendCount++; Pulse10HzCount++; }, FALLING);

  Serial.begin(115200);

  Pulse10HzCount = 0;

  SendCount = 0;

  Ms = 0;

  CmTraveled = 0;
}

void loop() 
{
    if (Ms == 0)
    {
      Ms = millis() + 100;
    }

    if (millis() >= Ms) //10Hz, used for speed control
    {
      Serial.print("P@10Hz=");
      Serial.print(Pulse10HzCount);
      Serial.print(",CM=");
      Serial.print(CmTraveled);
      Serial.print(",IN=");
      Serial.print(CmTraveled / 2.54);
      Serial.println();

      Ms = 0;
      Pulse10HzCount = 0;
    }

//2.54cm per in
//150p = 8.75cm for a total of 35cm in one revolution
//100p = 5.8333cm
//50p = 2.91666cm for a total of 35cm in one revolution - this may be a bit optimistic
  
  if (SendCount >= 100)
  {
    SendCount = 0;
    
    CmTraveled = CmTraveled + 5.8333; //cm
  }
}


