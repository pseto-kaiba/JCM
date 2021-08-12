using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace JCM_library
{

    public class JCMException : Exception
    {
        public JCMException()
        {
        }

        public JCMException(string message)
            : base(message)
        {
        }

        public JCMException(string message, Exception inner)
            : base(message, inner)
        {
        }
    }

    public enum AcceptorStatus
    {
        Idling = 0x11,
        Accepting = 0x12,
        Escrow = 0x13, //+DATA
        Stacking = 0x14,
        VendValid = 0x15,
        Stacked = 0x16,
        Rejecting = 0x17, //+DATA
        Returning = 0x18,
        Holding = 0x19,
        Inhibit = 0x1A,
        Initalize = 0x1B,
        PowerUp = 0x40,
        PowerUpBillAcceptor = 0x41,
        PowerUpBillStacker = 0x42,
        ErrorLow = 0x43,
        JamInAcceptor = 0x45,
        Failure = 0x49,
        ErrorHigh = 0x4B,
        Ack = 0x50
    }


    public class JCM_Controller
    {
        private SerialPort mySerialPort;
        public static readonly Dictionary<int,int> moneyCommands = new Dictionary<int, int>
        {
            {97, 10},
            {98, 20},
            {99, 50},
            {100, 100},
            {101, 200},
            {102, 500},
            {103, 1000},
            {104, 2000}
        };
        public static readonly Dictionary<string, string> statusMessages = new Dictionary<string, string>
        {
            {0x11.ToString(), "ENABLE (IDLING)" },
            {0x12.ToString(), "ACCEPTING" },
            {0x13.ToString(), "ESCROW" },
            {0x14.ToString(), "STACKING" },
            {0x15.ToString(), "VEND VALID" },
            {0x16.ToString(), "STACKED" },
            {0x17.ToString(), "REJECTING" },
            {0x18.ToString(), "RETURNING" },
            {0x19.ToString(), "HOLDING" },
            {0x1A.ToString(), "DISABLE (INHIBIT)" },
            {0x1B.ToString(), "INITIALIZE" }
        };
        public static readonly Dictionary<string, string> powerupMessages = new Dictionary<string, string>
        {
            {0x40.ToString(), "POWER UP" },
            {0x41.ToString(), "POWER UP WITH BILL IN ACCEPTOR" },
            {0x42.ToString(), "POWER UP WITH BILL IN STACKER" },
        };
        public static readonly Dictionary<string, string> errorMessages = new Dictionary<string, string>
        {
            {0x43.ToString(), "STACKER FULL" },
            {0x44.ToString(), "STACKER OPEN OR STACKER BOX REMOVED" },
            {0x45.ToString(), "JAM IN ACCEPTOR" },
            {0x46.ToString(), "JAM IN STACKER" },
            {0x47.ToString(), "PAUSE DUE TO SECOND BILL INSERTION PLEASE REMOVE SECOND BILL" },
            {0x48.ToString(), "CHEATING ERROR" },
            {0x49.ToString(), "UNKNOWN FAILURE" },
            {0x49.ToString()+":"+0xA2.ToString(), "STACK MOTOR FAILURE" },
            {0x49.ToString()+":"+0xA5.ToString(), "TRANSPORT MOTOR SPEED FAILURE" },
            {0x49.ToString()+":"+0xA6.ToString(), "TRANSPORT MOTOR FAILURE" },
            {0x49.ToString()+":"+0xA8.ToString(), "SOLENOID FAILURE" },
            {0x49.ToString()+":"+0xA9.ToString(), "PB UNIT FAILURE" },
            {0x49.ToString()+":"+0xAB.ToString(), "CASH BOX NOT READY" },
            {0x49.ToString()+":"+0xAF.ToString(), "VALIDATOR HEAD REMOVE" },
            {0x49.ToString()+":"+0xB0.ToString(), "BOOT ROM FAILURE" },
            {0x49.ToString()+":"+0xB1.ToString(), "EXTERNAL ROM FAILURE" },
            {0x49.ToString()+":"+0xB2.ToString(), "RAM FAILURE" },
            {0x49.ToString()+":"+0xB3.ToString(), "EXTERNAL ROM WRITING FAILURE" },
            {0x4A.ToString(), "COMMUNICATION FAILURE" },
            {0x4B.ToString(), "INVALID COMMAND" },
        };

        private static readonly Dictionary<string, byte[]> commands = new Dictionary<string, byte[]>
        {
            { "StatusReq", generateCommandWithCRC(new byte[] { 0xFC, 0x05, 0x11 }) },
            { "Ack", generateCommandWithCRC(new byte[] { 0xFC, 0x05, 0x50 }) },
            { "Reset", generateCommandWithCRC(new byte[] { 0xFC, 0x05, 0x40 }) },
            { "InhibitEnable", generateCommandWithCRC(new byte[] { 0xFC, 0x06, 0xC3, 0x1 }) },
            { "InhibitDisable", generateCommandWithCRC(new byte[] { 0xFC, 0x06, 0xC3, 0x0 }) },
            { "Stack-1", generateCommandWithCRC(new byte[] { 0xFC, 0x05, 0x41 }) },
        };

        //Helper functions
        private static string GetMsg(string key, Dictionary<string,string> dict)
        {
            if (dict.ContainsKey(key)) return dict[key];
            return null;
        }
        private static string byteToString(byte b)
        {
            return BitConverter.ToString(new byte[] {b}).Replace("-", string.Empty).ToUpper();
        }

        private static readonly string logFolder = "JCM_log";
        private static readonly string logFile = logFolder + "/log.txt";
        private Dictionary<int, int> acceptedCash;
        private readonly bool verbose;
        private int pollingCycle;
        private int errorRecovery;
        private byte[] buffer;
        private static (byte, byte) ComputeCRC(byte[] val, int len = -1, int offset = 0)
        {
            if (len < 0) len = val.Length;
            long crc;
            long q;
            byte c;
            crc = 0;
            for (int i = offset; i < len; i++)
            {
                c = val[i];
                q = (crc ^ c) & 0x0f;
                crc = (crc >> 4) ^ (q * 0x1081);
                q = (crc ^ (c >> 4)) & 0xf;
                crc = (crc >> 4) ^ (q * 0x1081);
            }
            long value = (byte)crc << 8 | (byte)(crc >> 8);

            string hex_value = string.Format("{0:x}", value).PadLeft(4, '0');
            byte low = Byte.Parse(hex_value.Substring(0, 2), System.Globalization.NumberStyles.HexNumber);
            byte high = Byte.Parse(hex_value.Substring(2, 2), System.Globalization.NumberStyles.HexNumber);

            return (low, high);
        }

        private static byte[] generateCommandWithCRC(byte[] command, int len = -1)
        {
            if (len < 0) len = command.Length;
            byte crcL, crcH;
            var crc = ComputeCRC(command, len: len);
            crcL = crc.Item1;
            crcH = crc.Item2;
            var new_command = new byte[len + 2];
            for (int i = 0; i < len; i++) new_command[i] = command[i];
            new_command[len] = crcL;
            new_command[len + 1] = crcH;
            return new_command;
        }
        public (Dictionary<int,int>, int) GetAcceptedCash()
        {
            int total_cash = 0;
            lock (acceptedCash)
            { 
                foreach (KeyValuePair<int, int> entry in acceptedCash)
                {
                    total_cash += entry.Key * entry.Value;
                }
            }
            return (acceptedCash, total_cash);
        }

        public void SetAcceptedCash(Dictionary<int,int> presetCashDict)
        {
            lock (acceptedCash)
            {
                acceptedCash = new Dictionary<int, int>
                {
                    {0, 0},
                    { 10, 0 },
                    { 20, 0 },
                    { 50, 0 },
                    { 100, 0 },
                    { 200, 0 },
                    { 500, 0 },
                    { 1000, 0 },
                    { 2000, 0 },
                };
                foreach (KeyValuePair<int, int> entry in presetCashDict)
                {
                    if (acceptedCash.ContainsKey(entry.Key)) acceptedCash[entry.Key] = entry.Value;
                }
            }
        }



        public void ResetAcceptedCash()
        {
            lock (acceptedCash)
            { 
                foreach (KeyValuePair<int, int> entry in acceptedCash)
                {
                    acceptedCash[entry.Key] = 0;
                }
            }
        }

        private string getErrorMessage(byte[] status)
        {
            if (status == null) return "COMMUNICATION FAILURE";
            string error = "";
            var errorType = status[2];
            //Failure with a specific subtype
            string key;
            if (errorType == 0x49)
            {
                key = 0x49.ToString() + ":" + status[3].ToString();
            }
            else key = errorType.ToString();
            error = errorMessages[key];
            return error;
        }

        private string state;
        private string data;
        private int novcanica;

        public JCM_Controller(string PortName, int BaudRate=9600, int ReadTimeout=50, int WriteTimeout=100, Dictionary<int, int> presetCashDict = null, bool verbose = false, int pollingCycle = 200, int errorRecovery = 5000)
        {
            Directory.CreateDirectory(logFolder);
            File.WriteAllText(logFile, string.Empty);
            mySerialPort = new SerialPort
            {
                PortName = PortName,
                BaudRate = BaudRate,
                DataBits = 8,
                StopBits = StopBits.One,
                Parity = Parity.Even,
                ReadTimeout = ReadTimeout,
                WriteTimeout = WriteTimeout,
                Handshake = Handshake.None
            };
            
            acceptedCash = new Dictionary<int, int>
            {
                {0, 0 },
                { 10, 0 },
                { 20, 0 },
                { 50, 0 },
                { 100, 0 },
                { 200, 0 },
                { 500, 0 },
                { 1000, 0 },
                { 2000, 0 },
            };
            if (presetCashDict != null)
            {
                SetAcceptedCash(presetCashDict);
            }
            buffer = new byte[1024];
            this.verbose = verbose;
            this.pollingCycle = pollingCycle;
            this.errorRecovery = errorRecovery;
            state = "";
            data = "";
            novcanica = 0;
        }


        private byte[] receiveData()
        {
            lock (mySerialPort)
            {
                try
                {
                    byte[] data = null;
                    mySerialPort.Open();
                    mySerialPort.Read(buffer, 0, 2);
                    int msg_len = buffer[1];
                    mySerialPort.Read(buffer, 2, msg_len - 2);
                    data = new byte[msg_len];
                    for (int i = 0; i < msg_len; i++) data[i] = buffer[i];
                    if (verbose)
                    {
                        string hex_value = BitConverter.ToString(data);
                        Console.WriteLine("Got data response: " + hex_value);
                    }
                    byte compL, compH;
                    var compCRC = ComputeCRC(data, len: msg_len - 2);
                    compL = compCRC.Item1;
                    compH = compCRC.Item2;
                    //CRC we actually got
                    byte recL, recH;
                    recL = data[msg_len - 2];
                    recH = data[msg_len - 1];
                    //Mismatch? Return null aka communication error
                    if (recL != compL || recH != compH) return null;
                    return data;
                }
                catch (Exception ex)
                {

                    if (verbose) Console.WriteLine("EXCEPTION: " + ex.ToString());
                    File.AppendAllText(logFile, ex.ToString() + "\n");
                    return null;
                }
                finally
                {
                    mySerialPort.Close();
                }
            }
        }


        // Posalji status request komandu, dobij odgovor. Ako time-outuje, probaj ponovo dok resetTimeOut ne istekne. Null return znaci da je doslo do greske u komunikaciji.
        // Greska moze biti istek resetTimeOut-a, ili nepodudarajuci CRC-ovi
        private byte[] sendStatusReqGetResponse(int resetTimeOut=3000)
        {
            lock (mySerialPort)
            {
                try
                {
                    mySerialPort.Open();
                    byte[] command = commands["StatusReq"];
                    if (verbose)
                    {
                        string hex_value = BitConverter.ToString(command);
                        Console.WriteLine("Sent status request: " + hex_value);
                    }    
                    mySerialPort.Write(command, 0, 5);
                    int temp = mySerialPort.ReadByte();
                    while (temp != 0xFC) temp = mySerialPort.ReadByte();
                    buffer[0] = 0xFC;
                    int msg_len = 0;
                    byte[] status = null;
                    long read_StartTime = DateTimeOffset.Now.ToUnixTimeMilliseconds();
                    while (DateTimeOffset.Now.ToUnixTimeMilliseconds() - read_StartTime < resetTimeOut)
                    {
                        try
                        {
                            mySerialPort.Read(buffer, 1, 1);
                            msg_len = buffer[1];
                            mySerialPort.Read(buffer, 2, msg_len - 2);
                            status = new byte[msg_len];
                            for (int i = 0; i < msg_len; i++) status[i] = buffer[i];
                            if (verbose)
                            {
                                string hex_value = BitConverter.ToString(status);
                                Console.WriteLine("Got status response: " + hex_value);
                            }
                            //Communication error status
                            if (status[2] == 0x4A) throw new JCMException("Comm error");
                            break;
                        }
                        catch (Exception ex)
                        {
                            if ((ex is TimeoutException && (DateTimeOffset.Now.ToUnixTimeMilliseconds() - read_StartTime < resetTimeOut)) || ex is JCMException)
                            {
                                if (verbose)
                                {
                                    if (verbose) Console.WriteLine("EXCEPTION: " + ex.ToString());
                                    string hex_value = BitConverter.ToString(command);
                                    Console.WriteLine("Sent status request: " + hex_value);
                                }
                                mySerialPort.Write(command, 0, 5);
                            }
                            else throw;
                        }
                    }
                    //CRC we computed from the received bytes
                    byte compL, compH;
                    var compCRC = ComputeCRC(status, len: msg_len - 2);
                    compL = compCRC.Item1;
                    compH = compCRC.Item2;
                    //CRC we actually got
                    byte recL, recH;
                    recL = status[msg_len - 2];
                    recH = status[msg_len - 1];
                    //Mismatch? Return null aka communication error
                    if (recL != compL || recH != compH) return null;
                    return status;
                }
                catch (Exception ex)
                {
                    if (verbose) Console.WriteLine("EXCEPTION: "+ ex.ToString());
                    File.AppendAllText(logFile, ex.ToString() + "\n");
                }
                finally
                {
                    mySerialPort.Close();
                }
            }
            return null;
        }

        private bool sendCommand(string commandName)
        {
            lock (mySerialPort)
            {
                try
                {
                    mySerialPort.Open();
                    byte[] command = commands[commandName];
                    mySerialPort.Write(command, 0, command.Length);
                }
                catch (Exception ex)
                {
                    if (verbose) Console.WriteLine(ex.ToString() + "\n");
                    File.AppendAllText(logFile, ex.ToString() + "\n");
                    return false;
                }
                finally
                {
                    mySerialPort.Close();
                }
            }
            return true;
        }


        public void resetJCM(byte[] status = null)
        {
            if (status == null) status = sendStatusReqGetResponse();
            string error = "";
            if (status == null) error = "COMMUNICATION FAILURE";
            else if (errorMessages.ContainsKey(status[2].ToString()))
            {
                error = getErrorMessage(status);
            }
            
            if (error != "")
            {
                if (verbose) Console.WriteLine("Error: " + error);
                File.AppendAllText(logFile, error + "\n");
                //throw new JCMException(error);
            }

            /*var sType = (AcceptorStatus) status[2];
            
            if (sType == AcceptorStatus.PowerUpBillAcceptor)
            {
                error = "Attempting to reset with bills in acceptor!";
                if (verbose) Console.WriteLine("Error: " + error);
                File.AppendAllText(logFile, error + "\n");
                throw new JCMException(error);
            }*/
            sendCommand("Reset");
        }

        public bool enableJCM(bool resetIfJustPowered = true)
        {
            if (resetIfJustPowered)
            {
                var status = sendStatusReqGetResponse();
                if (status == null) return false;
                var sType = (AcceptorStatus)status[2];
                if (sType == AcceptorStatus.PowerUp || sType == AcceptorStatus.PowerUpBillStacker) resetJCM();
            }
            return sendCommand("InhibitDisable");
        }

        public bool disableJCM()
        {
            return sendCommand("InhibitEnable");
        }

        public void work()
        {
            while (true)
            {
                try
                {
                    var status = sendStatusReqGetResponse();
                    //Ako dobijes comm error, probaj opet posle 5 sekundi
                    if (status == null)
                    {
                        if (verbose) Console.WriteLine("COMMUNICATION ERROR");
                        File.AppendAllText(logFile, "COMMUNICATION ERROR" + "\n");
                        Thread.Sleep(errorRecovery);
                        continue;
                    }
                    var sType = (AcceptorStatus)(status[2]);
                    // Ostali errori: javljas exception odmah i pali
                    if (errorMessages.ContainsKey(sType.ToString())) throw new JCMException(getErrorMessage(status));

                    // Masina je u ravnoteznom stanju, moze da prima novac
                    if (sType == AcceptorStatus.Idling && state != "ENABLE")
                    {
                        state = "ENABLE";
                    }

                    // Novcanica se zaglavila
                    if (sType == AcceptorStatus.JamInAcceptor && state == "REJECTING")
                    {
                        File.AppendAllText(logFile, "BILL STUCK" + "\n");
                        if (verbose) Console.WriteLine("BILL STUCK");
                        state = "JAM IN ACCEPTOR";
                    }

                    //Novcanica je zaglavljena, resetujmo
                    if (state == "JAM IN ACCEPTOR" && sType == AcceptorStatus.Inhibit)
                    {
                        File.AppendAllText(logFile, "BILL STUCK CLEARED, RE-ENABLING NOW" + "\n");
                        if (verbose) Console.WriteLine("BILL STUCK CLEARED, RE-ENABLING NOW");
                        enableJCM(resetIfJustPowered: false);
                    }

                    // Novcanica se razvrstava
                    if (state == "ENABLE" && sType == AcceptorStatus.Accepting)
                    {
                        state = "ACCEPTING";
                    }

                    // Novcanica moze da se primi
                    if (state == "ACCEPTING" && sType == AcceptorStatus.Escrow)
                    {
                        state = "ESCROW";
                        novcanica = moneyCommands[status[3]];
                        sendCommand("Stack-1");
                        //var ack = receiveData();
                    }

                    // Odbijeno stanje
                    if ((state == "ACCEPTING" || state == "STACKING") && sType == AcceptorStatus.Rejecting)
                    {
                        File.AppendAllText(logFile, "REJECTING BILL" + "\n");
                        if (verbose) Console.WriteLine("REJECTING BILL");
                        state = "REJECTING";
                    }

                    // Novcanica ulazi u steker
                    if (state == "ESCROW" && sType == AcceptorStatus.Stacking)
                    {
                        state = "STACKING";
                    }


                    //Reset struje tokom stekovanja
                    if (state == "STACKING" && sType == AcceptorStatus.PowerUpBillStacker)
                    {
                        File.AppendAllText(logFile, "POWER REBOOTED WITH BILL IN STACKER, RESETTING" + "\n");
                        if (verbose) Console.WriteLine("POWER REBOOTED WITH BILL IN STACKER, RESETTING");
                        state = "REBOOTED WITH BILL";
                        enableJCM(resetIfJustPowered: true);
                    }


                    //Novcanica je uspesno primljena
                    if (state == "STACKING" && sType == AcceptorStatus.VendValid)
                    {
                        state = "STACKED";
                        lock (acceptedCash)
                        {
                            acceptedCash[novcanica]++;
                        }
                        sendCommand("Ack");
                    }

                    
                    if (verbose)
                    {
                        var asString = string.Join(Environment.NewLine, acceptedCash);
                        File.AppendAllText(logFile, "CURRENT CASH STATE: " + asString + "\n");
                        Console.WriteLine("Ukupno stanje: " + GetAcceptedCash().Item2);
                    }
                    Thread.Sleep(pollingCycle);
                }
                catch (Exception ex)
                {
                    //if (verbose) Console.WriteLine("EXCEPTION: " + ex.ToString());
                    //File.AppendAllText(logFile, ex.ToString() + "\n");
                    //if (!(ex is TimeoutException)) throw;
                    //Thread.Sleep(pollingCycle);
                    throw;
                }
            }
        }

    }


}

