﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows.Forms;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections.Specialized;
using System.Net;
using System.Diagnostics;
using System.Threading;
using System.IO;
using System.Runtime.InteropServices;
using System.Drawing;
using CSharpAnalytics;

namespace MaplestoryLauncher
{
    public partial class MainWindow : Form
    {
        # region Login
        private void loginWorker_DoWork(object sender, DoWorkEventArgs e)
        {
            pingWorker.CancelAsync();
            while (pingWorker.IsBusy)
                Thread.Sleep(133);

            Debug.WriteLine("loginWorker starting");
            Thread.CurrentThread.Name = "Login Worker";
            e.Result = "";
            try
            {
                if (Properties.Settings.Default.loginMethod != (int)LoginMethod.QRCode)
                    this.bfClient = new BeanfunClient();
                this.bfClient.Login(this.accountInput.Text, this.pwdInput.Text, Properties.Settings.Default.loginMethod, this.qrcodeClass, this.service_code, this.service_region);
                if (this.bfClient.errmsg != null)
                    e.Result = this.bfClient.errmsg;
                else
                    e.Result = null;
            }
            catch (Exception ex)
            {
                e.Result = "登入失敗，未知的錯誤。\n\n" + ex.Message + "\n" + ex.StackTrace;
            }
        }

        private void loginWorker_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            if (Properties.Settings.Default.GAEnabled && this.timedActivity != null)
            {
                AutoMeasurement.Client.Track(this.timedActivity);
                this.timedActivity = null;
            }
            Debug.WriteLine("loginWorker end");

            if (e.Error != null)
            {
                UI.ShowError(e.Error.Message, HelperFunctions.UI.ErrorType.LoginFailed);
                return;
            }
            if ((string)e.Result != null)
            {
                UI.ShowError((string)e.Result, HelperFunctions.UI.ErrorType.LoginFailed);
                return;
            }
            
            if (Properties.Settings.Default.keepLogged && !this.pingWorker.IsBusy)
                this.pingWorker.RunWorkerAsync();
            status = LogInState.LoggedIn;
            UI.LoggedIn();
        }
        #endregion

        #region OTP
        enum GameState
        {
            Running,
            Run,
            Failed
        }

        private void getOtpWorker_DoWork(object sender, DoWorkEventArgs e)
        {
            pingWorker.CancelAsync();
            while (pingWorker.IsBusy)
                Thread.Sleep(133);

            Debug.WriteLine("getOtpWorker start");
            Thread.CurrentThread.Name = "GetOTP Worker";
            int index = (int)e.Argument;
            Debug.WriteLine("Count = " + this.bfClient.accountList.Count + " | index = " + index);

            e.Result = new object[3]; //{index, otp, gameRun}
            ref object resultIndex = ref ((object[])e.Result)[0];
            ref object resultOtp = ref ((object[])e.Result)[1];
            ref object resultGameRun = ref ((object[])e.Result)[2];
            
            resultIndex = index;
            if (this.bfClient.accountList.Count <= index)
            {
                resultIndex = -1;
                return;
            }
            Debug.WriteLine("call GetOTP");
            resultOtp = bfClient.GetOTP(Properties.Settings.Default.loginMethod, this.bfClient.accountList[index], this.service_code, this.service_region);
            Debug.WriteLine("call GetOTP done");
            if (resultOtp == null)
            {
                resultIndex = -1;
                return;
            }

            if (Properties.Settings.Default.GAEnabled)
            {
                try
                {
                    AutoMeasurement.Client.TrackEvent(Path.GetFileName(gamePaths.Get(service_name)), "processName");
                }
                catch
                {
                    Debug.WriteLine("invalid path:" + gamePaths.Get(service_name));
                }
            }

            bool? gameStarted = null;
            if (GameIsRunning(
                true,
                bfClient.accountList[index].sacc,
                new string(Convert.ToString(resultOtp).Where(c => char.IsLetter(c) || char.IsDigit(c)).ToArray()),
                ref gameStarted
            ))
            {
                resultGameRun = GameState.Running;
            }
            else if (gameStarted == true)
                resultGameRun = GameState.Run;
            else
                resultGameRun = GameState.Failed;
        }

        private void getOtpWorker_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            if (Properties.Settings.Default.GAEnabled && this.timedActivity != null)
            {
                AutoMeasurement.Client.Track(this.timedActivity);
                this.timedActivity = null;
            }
            Debug.WriteLine("getOtpWorker end");

            int resultIndex = (int)((object[])e.Result)[0];
            string resultOtp = Convert.ToString(((object[])e.Result)[1]);
            GameState resultGameRun = (GameState)((object[])e.Result)[2];

            if (resultGameRun == GameState.Failed)
                UI.GameRun(false);
            else
                UI.GameRun();
            
            if (e.Error != null)
            {
                UI.OtpGot("取得失敗");
                UI.ShowError(e.Error.Message);
                return;
            }
            if (resultIndex == -1)
            {
                UI.OtpGot("取得失敗");
                UI.ShowError(this.bfClient.errmsg);
            }
            else
            {
                /*int accIndex = accounts.SelectedItems[0].Index;
                string acc = this.bfClient.accountList[index].sacc;
                this.Text = "進行遊戲 - " + WebUtility.HtmlDecode(this.bfClient.accountList[index].sname);*/

                if (resultGameRun == GameState.Running)
                {
                    UI.OtpGot(resultOtp);
                    try
                    {
                        Clipboard.SetText(resultOtp);
                    }
                    catch
                    {

                    }
                }
                else
                    UI.OtpGot("");
            }

            if (Properties.Settings.Default.keepLogged && !this.pingWorker.IsBusy)
                this.pingWorker.RunWorkerAsync();
        }
        #endregion

        #region Ping
        private void pingWorker_DoWork(object sender, DoWorkEventArgs e)
        {
            Thread.CurrentThread.Name = "ping Worker";
            Debug.WriteLine("pingWorker start");
            const int WaitSecs = 60; // 1min

            while (Properties.Settings.Default.keepLogged)
            {
                if (this.pingWorker.CancellationPending)
                {
                    Debug.WriteLine("break due to cancel");
                    break;
                }

                if (this.getOtpWorker.IsBusy || this.loginWorker.IsBusy)
                {
                    Debug.WriteLine("ping.busy sleep 1s");
                    System.Threading.Thread.Sleep(1000 * 1);
                    continue;
                }

                if (this.bfClient != null)
                    this.bfClient.Ping();

                for (int i = 0; i < WaitSecs; ++i)
                {
                    if (this.pingWorker.CancellationPending)
                        break;
                    System.Threading.Thread.Sleep(1000 * 1);
                }
            }
        }

        private void pingWorker_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            Debug.WriteLine("ping.done");
        }
        #endregion

        /*private void qrWorker_DoWork(object sender, DoWorkEventArgs e)
        {
            this.bfClient = new BeanfunClient();
            string skey = this.bfClient.GetSessionkey();
            this.qrcodeClass = this.bfClient.GetQRCodeValue(skey, (bool)e.Argument);
        }

        private void qrWorker_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            this.loginMethodInput.Enabled = true;
            wait_qrWorker_notify.Visible = false;
            if (this.qrcodeClass == null)
                wait_qrWorker_notify.Text = "QRCode取得失敗";
            else
            {
                qrcodeImg.Image = qrcodeClass.bitmap;
                qrCheckLogin.Enabled = true;
            }
        }

        private void qrCheckLogin_Tick(object sender, EventArgs e)
        {
            if (this.qrcodeClass == null)
            {
                MessageBox.Show("QRCode not get yet");
                return;
            }
            int res = this.bfClient.QRCodeCheckLoginStatus(this.qrcodeClass);
            if (res != 0)
                this.qrCheckLogin.Enabled = false;
            if (res == 1)
            {
                loginButton_Click(null, null);
            }
            if (res == -2)
            {
                comboBox1_SelectedIndexChanged(null, null);
            }
        }*/
    }
}