﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Net.Mail;
using System.Text.RegularExpressions;
using System.Diagnostics;
using System.Reflection;

namespace MapleStoryLauncher
{
    public partial class MainWindow : Form
    {
        public MainWindow()
        {
            CheckMultipleInstances();
            InitializeComponent();
            //Form.CheckForIllegalCrossThreadCalls = false;

            Model();
            Controller_MainWindow();
            Controller_AccountInput();
            Controller_AddRemoveAccount();
            Controller_PasswordInput();
            Controller_RememberPwd();
            Controller_AutoLogin();
            Controller_LoginButton();
            Controller_PointsLabel();
            Controller_AccountListView();
            Controller_AutoSelect();
            Controller_AutoLaunch();
            Controller_GetOTPButton();
            Controller_OTPDisplay();
        }

        public BeanfunBroker beanfun = new();

        # region Login
        private QRCodeWindow qrcodeWindow = default;
        private readonly ReCaptchaWindow reCaptchaWindow = new(); //webView2 needs more time, initialized early
        private AppAuthWindow appAuthWindow = default;

        private class LoginWorkerArgs
        {
            public bool useQRCode;
            public string username;
            public string password;
        }

        private void loginWorker_DoWork(object sender, DoWorkEventArgs e)
        {
            LoginWorkerArgs args = (LoginWorkerArgs)e.Argument;
            if (args.useQRCode)
            {
                if (qrcodeWindow == default)
                    qrcodeWindow = new(this);
                qrcodeWindow.ShowDialog();
                e.Result = qrcodeWindow.GetResult();
                if (e.Result == default(BeanfunBroker.TransactionResult))
                    e.Cancel = true;
            }
            else
            {
                bool reCaptchaRequired = true;
                BeanfunBroker.TransactionResult result = beanfun.GetReCaptcha(ref reCaptchaRequired);
                if (result.Status == BeanfunBroker.TransactionResultStatus.Success)
                {
                    if(reCaptchaRequired)
                    {
                        reCaptchaWindow.SetAddress(result.Message);
                        Invoke(() =>
                        {
                            reCaptchaWindow.SetCookies(beanfun.GetAllCookies());
                            reCaptchaWindow.ShowDialog();
                        });
                        string reCaptchaResponse = reCaptchaWindow.GetResponse();
                        if (reCaptchaWindow.DialogResult == DialogResult.Cancel)
                            e.Cancel = true;
                        else
                            result = beanfun.Login(args.username, args.password, reCaptchaResponse);
                    }
                    else
                        result = beanfun.Login(args.username, args.password);
                }
                switch (result.Status)
                {
                    case BeanfunBroker.TransactionResultStatus.RequireAppAuthentication:
                        if (appAuthWindow == default)
                            appAuthWindow = new(this);
                        appAuthWindow.ShowDialog();
                        e.Result = appAuthWindow.GetResult();
                        if (e.Result == default(BeanfunBroker.TransactionResult))
                        {
                            //beanfun.LocalLogout();
                            e.Cancel = true;
                        }
                        break;
                    default:
                        e.Result = result;
                        break;
                }
            }

            if (!e.Cancel &&
                ((BeanfunBroker.TransactionResult)e.Result).Status == BeanfunBroker.TransactionResultStatus.Success)
            {
                e.Result = beanfun.GetGameAccounts();
                if (((BeanfunBroker.TransactionResult)e.Result).Status == BeanfunBroker.TransactionResultStatus.Success)
                    ((BeanfunBroker.TransactionResult)e.Result).Message = args.username;
            }
        }

        private void loginWorker_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            if (e.Cancelled)
                SyncEvents.CancelLogin();
            else
                switch (((BeanfunBroker.TransactionResult)e.Result).Status)
                {
                    case BeanfunBroker.TransactionResultStatus.Success:
                        gameAccounts = ((BeanfunBroker.GameAccountResult)e.Result).GameAccounts;
                        pingTimer.Interval = PING_INTERVAL;
                        pingTimer.Start();
                        SyncEvents.SucceedLogin(((BeanfunBroker.TransactionResult)e.Result).Message);
                        break;
                    default:
                        ShowTransactionError((BeanfunBroker.TransactionResult)e.Result);
                        SyncEvents.CancelLogin();
                        break;
                }
        }
        #endregion

        #region Points
        private void getPointsWorker_DoWork(object sender, DoWorkEventArgs e)
        {
            e.Result = beanfun.GetRemainingPoints();
        }

        private void getPointsWorker_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            SyncEvents.UpdatePoints((int)e.Result);
        }
        #endregion

        #region Ping
        private const int PING_INTERVAL = 10 * 60 * 1000; //10 mins
        private const int PING_MAX_FAILED_TRIES = 5;
        private int pingFailedTries = 0;

        private void pingTimer_Tick(object sender, EventArgs e)
        {
            if (!pingTimer.Enabled)
                return;

            BeanfunBroker.TransactionResult result = beanfun.Ping(); //beanfun has reader-writer lock
            switch (result.Status)
            {
                case BeanfunBroker.TransactionResultStatus.Failed:
                    pingTimer.Stop();
                    beanfun.Logout();
                    ShowTransactionError(result);
                    SyncEvents.LogOut(loggedInUsername, false);
                    break;
                case BeanfunBroker.TransactionResultStatus.ConnectionLost:
                    if (++pingFailedTries >= PING_MAX_FAILED_TRIES)
                    {
                        pingTimer.Stop();
                        beanfun.Logout();
                        ShowTransactionError(result);
                        SyncEvents.LogOut(loggedInUsername, false);
                    }
                    pingTimer.Interval = 3;
                    break;
                case BeanfunBroker.TransactionResultStatus.LoggedOutByBeanfun:
                    pingTimer.Stop();
                    ShowTransactionError(result);
                    SyncEvents.LogOut(loggedInUsername, false);
                    break;
                case BeanfunBroker.TransactionResultStatus.Success:
                    pingTimer.Interval = PING_INTERVAL;
                    break;
            }
        }
        #endregion

        #region OTP
        //Arguments:
        //e.Argument: gameAccount
        private void getOTPWorker_DoWork(object sender, DoWorkEventArgs e)
        {
            BeanfunBroker.TransactionResult ping = beanfun.Ping(); //beanfun has reader-writer lock
            if (ping.Status != BeanfunBroker.TransactionResultStatus.Success)
            {
                e.Result = ping;
                return;
            }

            BeanfunBroker.GameAccount gameAccount = (BeanfunBroker.GameAccount)e.Argument;
            e.Result = beanfun.GetOTP(gameAccount); //beanfun has reader-writer lock
        }

        private void getOTPWorker_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            BeanfunBroker.TransactionResult result = (BeanfunBroker.TransactionResult)e.Result;
            switch (result.Status)
            {
                case BeanfunBroker.TransactionResultStatus.Success:
                    if (IsGameRunning())
                        SyncEvents.FinishGettingOTP(result.Message);
                    else
                    {
                        SyncEvents.FinishGettingOTP("");
                        SyncEvents.LaunchGame();
                        StartGame(((BeanfunBroker.OTPResult)result).ArgPrefix, ((BeanfunBroker.OTPResult)result).Username, result.Message);
                        SyncEvents.FinishLaunchingGame();
                    }
                    break;
                case BeanfunBroker.TransactionResultStatus.LoggedOutByBeanfun:
                    pingTimer.Stop();
                    ShowTransactionError(result);
                    SyncEvents.FinishGettingOTP("");
                    SyncEvents.LogOut(loggedInUsername, false);
                    break;
                default:
                    ShowTransactionError(result);
                    SyncEvents.FinishGettingOTP("");
                    break;
            }
        }
        #endregion
    }
}