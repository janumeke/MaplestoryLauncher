﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MapleStoryLauncher
{
    public partial class AppAuthWindow : Form
    {
        readonly MainWindow MainWindow;

        public AppAuthWindow(MainWindow handle)
        {
            MainWindow = handle;
            InitializeComponent();
        }

        private BeanfunBroker.TransactionResult result = default;

        public BeanfunBroker.TransactionResult GetResult()
        {
            lock (this)
            {
                return result;
            }
        }

        private const int maxFailedTries = 3;
        private int failedTries = 0;

        private void checkAppAuthStatusTimer_Tick(object sender, EventArgs e)
        {
            BeanfunBroker.TransactionResult checkResult = MainWindow.beanfun.CheckAppAuthentication();
            switch (checkResult.Status)
            {
                case BeanfunBroker.TransactionResultStatus.RequireAppAuthentication:
                    failedTries = 0;
                    break;
                case BeanfunBroker.TransactionResultStatus.ConnectionLost:
                    if (++failedTries >= maxFailedTries)
                    {
                        lock (this)
                        {
                            result = checkResult;
                        }
                        Close();
                    }
                    break;
                default:
                    lock (this)
                    {
                        result = checkResult;
                    }
                    Close();
                    break;
            }
        }

        private void AppAuthWindow_FormClosing(object sender, FormClosingEventArgs e)
        {
            checkAppAuthStatusTimer.Enabled = false;
            if (result == default || //manual closing while pending
                result.Status == BeanfunBroker.TransactionResultStatus.ConnectionLost)
                MainWindow.beanfun.LocalLogout();
        }
    }
}
