

#define interruptPin 2 // pulse Counter input

volatile int SendCount = 0;

volatile long PulseCount = 0;

volatile float CmTraveled = 0;

volatile float InTraveled = 0;

volatile float LastInTraveled = 0;

volatile int Ms = 0;

void setup() 
{ 
  pinMode(interruptPin, INPUT_PULLUP);
  
  // put your setup code here, to run once:
  attachInterrupt(digitalPinToInterrupt(interruptPin), [](){ SendCount++; PulseCount++; }, FALLING);

  Serial.begin(115200);

  PulseCount = 0;

  SendCount = 0;

  Ms = 0;

  CmTraveled = 0;

  InTraveled = 0;

  LastInTraveled = 0;
}

void loop() 
{
    if (Ms == 0)
    {
      Ms = millis() + 100;
    }

    if (millis() >= Ms) 
    {
      Serial.print("IN=");
      Serial.print(InTraveled);
      Serial.print(",FPS=");
      Serial.print(((InTraveled - LastInTraveled) / 12) * 10);
      Serial.println();

      Ms = 0;
      PulseCount = 0;
      LastInTraveled = InTraveled;
    }

//2.54cm per in
//150p = 8.75cm for a total of 35cm in one revolution
//100p = 5.8333cm
//50p = 2.91666cm for a total of 35cm in one revolution - this may be a bit optimistic
  
  if (SendCount >= 100)
  {
    SendCount = 0;
    
    CmTraveled = CmTraveled + 5.3333; //cm

    InTraveled = CmTraveled / 2.54;
  }
}


