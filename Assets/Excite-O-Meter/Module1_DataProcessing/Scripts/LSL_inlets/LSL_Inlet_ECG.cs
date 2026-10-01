using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace ExciteOMeter
{
    public class LSL_Inlet_ECG : InletIntSamples
    {
        void Start()
        {
            // [optional] call this only, if your gameobject hosting this component
            // got instantiated during runtime
            
            // registerAndLookUpStream();
        }

        /// <summary>
            /// Override this method to implement whatever should happen with the samples...
            /// IMPORTANT: Avoid heavy processing logic within this method, update a state and use
            /// coroutines for more complexe processing tasks to distribute processing time over
            /// several frames
            /// </summary>
            /// <param name="newSample"></param>
            /// <param name="timeStamp"></param>
        protected override void Process(int[] newSample, double timestamp)
        {
            if (newSample == null || newSample.Length == 0)
                return;

            // Debug.Log($"Receiving from stream {StreamName}, first sample {newSample[0]}");

            //TODO: The event only sends float[], all samples need to be parsed to float
            
            if(Math.Abs((double)newSample[0]) > 1.0e7)
            {
                Debug.Log("Error parsing value ECG: " + BitConverter.ToString(BitConverter.GetBytes(newSample[0])) + ", " + newSample[0].ToString("F2"));
            }
            else
            {
                EoM_Events.Send_OnDataReceived(VariableType, ExciteOMeterManager.GetTimestamp(), newSample[0]);            
                LoggerController.instance?.WriteLine(LogName.VariableRawECG, ExciteOMeterManager.GetTimestampString() + "," + newSample[0].ToString("F0"));
            }
        }
    }
}
