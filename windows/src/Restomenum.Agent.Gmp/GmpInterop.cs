#nullable disable
using Newtonsoft.Json;
using System.Runtime.InteropServices;
using System.Text;

namespace Restomenum.Agent.Gmp.Interop
{

    public class ST_GMP_PAIR
    {
        public string szProcOrderNumber;
        public string szProcDate;
        public string szProcTime;
        public string szExternalDeviceBrand;
        public string szExternalDeviceModel;
        public string szExternalDeviceSerialNumber;
        public string szEcrSerialNumber;

        public ST_GMP_PAIR()
        {
            szProcOrderNumber = "";
            szProcDate = "";
            szProcTime = "";
            szExternalDeviceBrand = "";
            szExternalDeviceModel = "";
            szExternalDeviceSerialNumber = "";
            szEcrSerialNumber = "";
        }
    };

    public class ST_GMP_PAIR_RESP
    {
        public UInt32 ErrorCode;
        public string szEcrBrand;
        public string szEcrModel;
        public string szEcrSerialNumber;
        public string szVersionNumber;

        public ST_GMP_PAIR_RESP()
        {
            szEcrBrand = "";
            szEcrModel = "";
            szEcrSerialNumber = "";
            ErrorCode = 0;
            szVersionNumber = "";
        }
    };

    public class ST_ECHO
    {
        public UInt32 retcode;
        public UInt32 status;
        public byte[] kvc;
        public byte ecrMode;
        public UInt16 mtuSize;
        public byte[] ecrVersion;
        public ST_DATE date;
        public ST_TIME time;
        public ST_CASHIER activeCashier;

        public ST_ECHO()
        {
            kvc = new byte[8];
            ecrMode = 0;
            ecrVersion = new byte[16];
            activeCashier = new ST_CASHIER();
            date = new ST_DATE();
            time = new ST_TIME();
        }
    };

    public class _ST_PAYMENT_REQUEST_ORGINAL_DATA
    {
        public UInt32 TransactionAmount;              /**< tag 21 OrgTransAmount[6] bcd */
        public UInt32 LoyaltyAmount;                  /**< tag 25 OrgLoyaltyAmount[6] bcd */
        public UInt16 NumberOfinstallments;           /**< tag 22 Number of installments, Zero if not used */
        public byte[] AuthorizationCode;			    /**< tag 45 ascii */
        public byte[] rrn; 						        /**< tag 46 ascii */
        public byte[] TransactionDate;                /**< tag 47 OrgTransDate[5] bcd YY- YYMMDDHHMM */
        public byte[] MerchantId;					    /**< tag 67 ascii */
        public byte TransactionType;                  /**< tag 70 byte */
        public byte[] referenceCodeOfTransaction;	    /**< tag 75 ascii */
    };

    public class ST_PAYMENT_RESPONSE
    {
        public byte flags;
        public UInt32 dateOfPayment;
        public UInt32 typeOfPayment;				// EPaymentTypes
        public byte subtypeOfPayment;			    // EPaymentSubtypes
        public UInt32 orgAmount;					// Exp; Currency Amount
        public UInt16 orgAmountCurrencyCode;		// as defined in currecyTable from GIB
        public UInt32 payAmount;					// always TL with precision 2
        public UInt16 payAmountCurrencyCode;		// always TL
        public UInt32 cashBackAmountInTL;			// Para üstü, her zaman TL with precision 2
        public UInt32 cashBackAmountInDoviz;		// Para Üstü, döviz satış ise döviz karşılığı
        public string paymentName;			        // Payment name written on the ticket */
        public string paymentInfo;			        // Payment sub message acording to the payment type */
        public ST_BANK_PAYMENT_INFO stBankPayment;	// Keeps all payment info related with bank

        public ST_PAYMENT_RESPONSE()
        {
            paymentName = "";
            paymentInfo = "";
            stBankPayment = new ST_BANK_PAYMENT_INFO();
        }
    }

    public class ST_PAYMENT_REQUEST
    {
        public UInt32 typeOfPayment;
        public UInt32 subtypeOfPayment;
        public UInt32 payAmount;
        public UInt32 payAmountBonus;
        public UInt16 payAmountCurrencyCode;
        public UInt16 bankBkmId;
        public UInt16 numberOfinstallments;
        public byte[] terminalId;
        public string BankPaymentUniqueId;

        public _ST_PAYMENT_REQUEST_ORGINAL_DATA OrgTransData;

        public UInt32 batchNo;
        public UInt32 stanNo;
        public UInt16 rawDataLen;
        public byte[] rawData;
        public string paymentName;
        public string paymentInfo;

        public UInt32 transactionFlag;
        public UInt32 flags;

        public string LoyaltyCustomerId;
        public string PaymentProvisionId;
        public UInt16 LoyaltyServiceId;

        public ST_PAYMENT_REQUEST()
        {
            terminalId = new byte[8];
            rawDataLen = 0;
            rawData = new byte[512];
            BankPaymentUniqueId = "";
            OrgTransData = new _ST_PAYMENT_REQUEST_ORGINAL_DATA();
        }
    };

    public struct ST_EcrSettings
    {
        public byte InvoiceSettings;
        public byte Z_Settings;
        public UInt16 Z_Time_In_Minute;
        public byte Copy_Button_Secured;
        public UInt16 Backlight_Timeout;
        public UInt16 Backlight_Level;
        public UInt16 Keylock_Timeout;
    };

    public class ST_EXCHANGE_PROFILE
    {
        public string ProfileName;
        public byte NumberOfCurrency;
        public ST_EXCHANGE [] ExchangeRecords;

        public ST_EXCHANGE_PROFILE()
        {
            ProfileName = "";
            ExchangeRecords = new ST_EXCHANGE[6];
            for (int i = 0; i < 6; i++)
                ExchangeRecords[i] = new ST_EXCHANGE();
        }

        public bool isEmptyFields()
        {
            if (string.IsNullOrWhiteSpace(ProfileName) || NumberOfCurrency == 0)
                return true;
            return false;
        }
    };
    

    public class ST_EXCHANGE
    {

        public UInt16 code;
        public string prefix;
        public string sign;
        public byte locationOfSign;
        public byte tousandSeperator;
        public byte centSeperator;
        public byte numberOfCentDigit;
        public UInt64 rate;

        public ST_EXCHANGE()
        {
            prefix = "";
            sign = "";
        }
    };

    public class ST_PaymentErrMessage
    {
        public string ErrorCode;        // bank error code
        public string ErrorMsg;
        public string AppErrorCode;     // payment application specific error code
        public string AppErrorMsg;

        public ST_PaymentErrMessage()
        {
            ErrorCode = "";             // bank error code
            ErrorMsg = "";
            AppErrorCode = "";          // payment application specific error code
            AppErrorMsg = "";
        }
    };

    public class ST_BankSubPaymentInfo
    {
        public UInt16 type; 				// EPaymentSubType
        public UInt32 amount;
        public string name;

        public ST_BankSubPaymentInfo()
        {
            name = "";
        }
    };

    public class ST_BANK_PAYMENT_INFO
    {
        public UInt32 batchNo;
        public UInt32 stan;
        public UInt32 balance;
        public UInt16 bankBkmId;
        public byte numberOfdiscount;
        public byte numberOfbonus;
        public string authorizeCode;
        public byte[] transFlag;
        public string terminalId;
        public string rrn;
        public string merchantId;
        public string bankName;
        public byte numberOfInstallments;
        public byte numberOfsubPayment;
        public byte numberOferrorMessage;
        public ST_BankSubPaymentInfo[] stBankSubPaymentInfo;
        public ST_CARD_INFO stCard;
        public ST_PaymentErrMessage stPaymentErrMessage;

        public ST_BANK_PAYMENT_INFO()
        {
            authorizeCode = "";
            transFlag = new byte[2];
            terminalId = "";
            merchantId = "";
            bankName = "";

            stBankSubPaymentInfo = new ST_BankSubPaymentInfo[12];
            stCard = new ST_CARD_INFO();
            stPaymentErrMessage = new ST_PaymentErrMessage();
        }
    };

    public class ST_PAYMENT
    {
        public byte flags;
        public UInt32 dateOfPayment;
        public UInt32 typeOfPayment;				// EPaymentTypes
        public byte subtypeOfPayment;			    // EPaymentSubtypes
        public UInt32 orgAmount;					// Exp; Currency Amount
        public UInt16 orgAmountCurrencyCode;		// as defined in currecyTable from GIB
        public UInt32 payAmount;					// always TL with precision 2
        public UInt16 payAmountCurrencyCode;		// always TL
        public UInt32 cashBackAmountInTL;			// Para üstü, her zaman TL with precision 2
        public UInt32 cashBackAmountInDoviz;		// Para Üstü, döviz satış ise döviz karşılığı
        public string paymentName;			        // Payment name written on the ticket */
        public string paymentInfo;			        // Payment sub message acording to the payment type */
        public ST_BANK_PAYMENT_INFO stBankPayment;	// Keeps all payment info related with bank


        public ST_PAYMENT()
        {
            paymentName = "";
            paymentInfo = "";
            stBankPayment = new ST_BANK_PAYMENT_INFO();
        }
    };

    public class ST_printerDataForOneLine
    {
        public UInt32 Flag;
        public byte lineLen;
        public string line;

        public ST_printerDataForOneLine()
        {
            line = "";
        }
    };

    public class ST_SALEINFO
    {
        public byte ItemType;
        public UInt64 ItemPrice;
        public UInt64 IncAmount;
        public UInt64 DecAmount;
        public UInt32 OrigialItemAmount; // Eğer kısım bilgisi TL olarak tanımlanmamışsa, kısım tutarı buraya kaydedilir ve diğer amout yeniden kur bilgisi ile hesaplanılarak ezilir
        public UInt16 OriginalItemAmountCurrency;
        public UInt16 ItemVatRate;
        public UInt16 ItemCurrencyType;
        public byte ItemVatIndex;
        public byte ItemCountPrecision;
        public int ItemCount;
        public byte ItemUnitType;
        public byte DeptIndex;
        public UInt32 Flag;
        public string Name;
        public string Barcode;

        public ST_SALEINFO()
        {
            Name = "";
            Barcode = "";
        }
    } ;

    public class ST_VATDetail
    {
        public int u32VAT;						/**< Total Tax in TL with precition 2 */
        public int u32Amount;					/**< Total Amount in TL with precition 2 */
        public UInt16 u16VATPercentage;			/**< Tax rate, it is 1800 for %18 */

        public ST_VATDetail()
        {
            u32VAT = 0;
            u32Amount = 0;
            u16VATPercentage = 0;
        }
    };

    public class Z_department
    {
        public long totalAmount;
        public long totalQuantity;
        public byte[] name;
        public Z_department()
        {
            name = new byte[25];
        }
    } ;

    public class Z_exchange
    {
        public long totalAmount;
        public long totalAmountInTL;
        public byte[] name;
        public Z_exchange()
        {
            name = new byte[13];
        }
    }

    public class Z_tax
    {
        public long totalAmount;
        public long totalTax;
        public UInt16 taxRate;
    }

    public class Z_cashier
    {
        public long totalAmount;
        public byte[] name;
        public Z_cashier()
        {
            name = new byte[12];
        }
    }

    public struct Z_countOf									    /**< Counters based data*/
    {
        public int increaments;									/**< int 999999  , Total number of increases */
        public int decreases; 									/**< int 999999  , Total number of decreases */
        public int corrections; 								/**< int 999999  , Total number of corrections */
        public int fiscalReceipts; 								/**< int 999999  , Total number of Fiscal Tickets */
        public int nonfiscalReceipts; 							/**< int 999999  , Total number of non Fiscal Tickets */
        public int customerReceipts; 							/**< int 999999	 , Total number of Tickets */
        public int voidReceipts; 								/**< int 999999  , Total number of Void Tickets */
        public int invoiceSaleReceipts; 						/**< int 999999  , Total number of Invoice Tickets */
        public int yemekcekiReceipts; 							/**< int 999999  , Total number of YemekCeki Tickets */
        public int carParkingEntryReceipts;						/**< int 999999  , Total number of CarParking Tickets */
        public int fiscalReportReceipts;						/**< int 999999  , Total number of FiscalReport Ticket counts */
        public int tasnifDisiReceipts; 							/**< int 999999  , Total number of info Ticket counts */
        public int invoiceReceipts; 							/**< int 999999  , Total number of Invoice Ticket counts */
        public int matrahsizReceipts; 							/**< int 999999  , Total number of Matrahsiz Ticket counts */
        public int serviceModeEntry; 							/**< int 999999  , Total number of entries into Service Menu of ECR */
        public UInt32 advanceReceipts;	 						/**< uint32 999999  , Total number of Advance transactions */
        public UInt32 openAccountReceipts;						/**< uint32 999999  , Total number of Open Account transaction */
    } ;

    public struct Z_invoice
    {
        public long TotalAmount;
        public long classicTotalAmount;
        public long e_invoiceTotalAmount;
        public long e_archiveTotalAmount;
        public long creditTotalAmount;
        public long cashTotalAmount;
    } ;

    public struct Z_other
    {
        public long mobil;
        public long hediyeCeki;
        public long ikram;
        public long odemesiz;
        public long kapora;
        public long puan;
        public long giderPusulasi;
        public long cek;
        public long bankaTransfer;
    } ;

    public class Z_payment
    {
        public long cashTotal;
        public long creditTotal;
        public long otherTotal;

        public Z_other other;
        public Z_payment()
        {
            other = new Z_other();
        }
    }

    public struct Z_sale
    {
        public UInt16 KoltukSayisi;
        public UInt16 reserved;
        public long totalAmount;
        public long totalTax;
    }
    public struct Z_refund
    {
        public UInt16 KoltukSayisi;
        public UInt16 reserved;
        public long totalAmount;
        public long totalTax;
    }

    public struct Z_invoiceSale
    {
        public UInt16 KoltukSayisi;
        public UInt16 reserved;
        public long totalAmount;
        public long totalTax;
    }

    public class Z_cinema
    {
        public Z_sale sale;
        public Z_refund refund;
        public Z_invoiceSale invoiceSale;
    }

    public class Z_sectorData
    {
        public Z_cinema[] cinema;
        public Z_sectorData()
        {
            cinema = new Z_cinema[Defines.MAX_CINEMA_DEPARTMENT_COUNT];
        }
    }

    #region Aylik Rapor
    public struct ST_KDV_Grubu
    {
        public string VergiToplamTutari;
        public string VergiOrani;
        public string VergiToplamKDV;
    }

    public struct ST_OKC_BelgeTipi
    {
        public string ToplamAdedi;
        public string KDV_Toplami;
        public string SatisTutariToplami;
    };
    
    public struct ST_BilgiFisi
    {
        public string Adedi;
	    public string ToplamTutari;
    };

    public struct ST_BilgiFisleri
    {
        public ST_BilgiFisi stFaturaBilgi;
        public ST_BilgiFisi stYemekKarti;
        public ST_BilgiFisi stAvans;
        public ST_BilgiFisi stFaturaTahsilati;
        public ST_BilgiFisi stCariHesap;
        public ST_BilgiFisi stDiger;
        public ST_BilgiFisi stGenelToplam;
        public string OtoparkFisiAdedi;
        public string MaliFisYemekKartiTutari;
        public string MaliFisFaturaTahsilatTutari;
        public string MaliFisDigerMatrahsiz;
    };

    public struct ST_Diger
    {
        public ST_OKC_BelgeTipi stFatura;
        public ST_OKC_BelgeTipi stSMM;
        public ST_OKC_BelgeTipi stGiderPusulasi;
        public ST_OKC_BelgeTipi stMM;
        public ST_OKC_BelgeTipi stBilet;
    };

    public struct ST_OKC_Belge
    {
        public ST_OKC_BelgeTipi stOKCFisi;
        public ST_BilgiFisleri stBilgiFisleri;
        public ST_Diger stDiger;
    };
        
    public struct ST_OdemeToplami
    {
        public string Nakit;
        public string KrediKarti;
        public string SanalPos;
        public string HediyeKarti;
        public string HavaleEFT;
        public string E_ParaHizliPara;
        public string SenetCek;
        public string KrediliVadeliAcikHesap;
        public string YemekKarti;
        public string Diger;
    }

    public class ST_DM_REPORT
    {
        public int StructSize;
        public UInt16 versiyon;
        public string IsyeriVKN;
        public string RaporUretilmeTarihi;
        public string RaporUretilmeSaati;
        public string AylikRaporNo;
        public string GunlukRaporNo;
        public string RaporDonemiBaslangicTarihi;
        public string RaporDonemiBitisTarihi;
        public string KDV_GrupAdedi;
        public ST_KDV_Grubu[] stKDV_Grubu = new ST_KDV_Grubu[Defines.MAX_TAXRATE_COUNT];
        public string ToplamKDV_Tutari;
        public string ToplamSatisTutari;
        public string BeyanEdilecekKDV_Tutari;
        public string IndirimToplamTutari;
        public string ArtirimToplamTutari;
        public string KumulatifToplamKDV_Tutari;
        public string KumulatifToplamSatisTutari;
        public string IptalEdilenBelgeAdedi;
        public string IptalEdilenBelgeToplamTutari;
        public ST_OKC_Belge stOKC_Belge = new ST_OKC_Belge();
        public ST_OdemeToplami stOdemeToplami;
        public string EkuNo;
    }
    #endregion

    public class ST_Z_REPORT
    {
        public int StructSize;
        public byte[] Date;
        public byte[] Time;
        public int FNo;
        public int ZNo;
        public int EJNo;
        public Z_countOf countOf;
        public long IncTotAmount;
        public long DecTotAmount;
        public long SaleVoidTotAmount;
        public long CorrectionTotAmount;
        public long InvoiceSaleTotAmount;
        public long FoodRcptTotAmount;
        public long DailyTotAmount;
        public long DailyTotTax;
        public long CumulativeTotAmount;
        public long CumulativeTotTax;
        public long AvansTotalAmount;
        public long OdemeTotalAmount;
        public long TaxRefundTotalAmount;
        public long MatrahsizTotalAmount;
        public long OpenAccountTotalAmount;							/**< uint64 999999999999 , Total Amount of Open Account transaction */

        public Z_department[] department;
        public Z_exchange[] exchange;
        public Z_tax[] tax;
        public Z_cashier[] cashier;
        public Z_invoice invoice;
        public Z_payment payment;
        public Z_sectorData sectorData;

        public ST_Z_REPORT()
        {
            Date = new byte[3];
            Time = new byte[2];
            countOf = new Z_countOf();
            department = new Z_department[Defines.MAX_DEPARTMENT_COUNT];
            exchange = new Z_exchange[Defines.MAX_EXCHANGE_COUNT];
            tax = new Z_tax[Defines.MAX_TAXRATE_COUNT];
            cashier = new Z_cashier[Defines.MAX_CASHIER_COUNT];
            invoice = new Z_invoice();
            payment = new Z_payment();
            sectorData = new Z_sectorData();
        }
    }

    public class ST_TICKET
    {
        public UInt32 TransactionFlags;
        public UInt32 OptionFlags;
        public UInt16 ZNo;
        public UInt16 FNo;
        public UInt16 EJNo;
        public UInt32 TotalReceiptAmount;
        public UInt32 TotalReceiptTax;
        public UInt32 TotalReceiptDiscount;
        public UInt32 TotalReceiptIncrement;
        public UInt32 CashBackAmount;
        public UInt32 TotalReceiptItemCancel;
        public UInt32 TotalReceiptPayment;
        public UInt32 TotalReceiptReversedPayment;
        public UInt32 KasaAvansAmount;
        public UInt32 KasaPaymentAmount;
        public UInt32 invoiceAmount;
        public UInt32 invoiceAmountCurrency;
        public UInt32 KatkiPayiAmount;
        public UInt32 TaxFreeRefund;
        public UInt32 TaxFreeCalculated;
        public string szTicketDate;
        public string szTicketTime;
        public UInt16 SourceVasAppID;
        public UInt16 PaymentVasAppID;
        public UInt16 BankVasAppID;
        public byte ticketType;
        public UInt16 totalNumberOfItems;
        public UInt16 numberOfItemsInThis;
        public UInt16 totalNumberOfPayments;
        public UInt16 numberOfPaymentsInThis;
        public UInt16 numberOfLoyaltyInThis;
        public string TckNo;
        public string invoiceNo;
        public UInt32 invoiceDate;
        public byte invoiceType;
        public int totalNumberOfPrinterLines;
        public int numberOfPrinterLinesInThis;
        public byte[] uniqueId;
        public byte[] rawData;
        public UInt16 rawDataLen;
        public string LastPaymentErrorCode;        // bank error code
        public string LastPaymentErrorMsg;         // bank error message
        public string BankPaymentUniqueId;
        public ST_SALEINFO[] SaleInfo;
        public ST_PAYMENT[] stPayment;
        public ST_VATDetail[] stTaxDetails;
        public ST_printerDataForOneLine[] stPrinterCopy;
        public byte[] UserData;
        public ST_LOYALTY_SERVICE_INFO[] stLoyaltyService;
        public int CurrencyProfileIndex;

        public ST_TICKET()
        {
            TckNo = "";
            invoiceNo = "";
            szTicketDate = "";
            szTicketTime = "";
            uniqueId = new byte[24];
            rawData = new byte[512];
            SaleInfo = new ST_SALEINFO[512];
            stPayment = new ST_PAYMENT[24];
            stTaxDetails = new ST_VATDetail[8];
            stPrinterCopy = new ST_printerDataForOneLine[1024];
            stLoyaltyService = new ST_LOYALTY_SERVICE_INFO[Defines.MAX_LOYALITY_TRANS_NUMBER];
        }
    };

    public class ST_ITEM
    {
        public byte type { get; set; }
        public byte subType { get; set; }
        public byte deptIndex { get; set; }
        public int taxRate { get; set; }
        public byte unitType { get; set; }
        public UInt32 amount { get; set; }
        public UInt16 currency { get; set; }
        public UInt32 count { get; set; }
        public UInt32 flag { get; set; }
        public byte countPrecition { get; set; }
        public byte pluPriceIndex { get; set; }
        public string name { get; set; }
        public string barcode { get; set; }
        public string? firm { get; set; }
        public string? invoiceNo { get; set; }
        public string? subscriberId { get; set; }
        public string? tckno { get; set; }
        public UInt32 Reserved { get; set; }
        public byte[]? Date { get; set; }
        //public UInt32 date;
        public promotion? promotion { get; set; }
        public UInt16 OnlineInvoiceItemExceptionCode { get; set; }

        //public ST_ITEM()
        //{
        //    name = "";
        //    barcode = "";
        //    firm = "";
        //    invoiceNo = "";
        //    subscriberId = "";
        //    tckno = "";
        //    promotion = new promotion();
        //    Date = new byte[3];
        //}

    };

    public class ST_INTERFACE_XML_DATA
    {
        public byte RetryCounter;
        public byte IpRetryCount;
        public UInt32 AckTimeOut;
        public UInt32 CommTimeOut;
        public UInt32 InterCharacterTimeOut;

        public string PortName;
        public int BaudRate;
        public int ByteSize;
        public int fParity;
        public int Parity;
        public int StopBit;
        public byte IsTcpConnection;
        public string IP;
        public int Port;
    };

    public class ST_HANDLE_LIST
    {

        public UInt64 Handle;
        public string szMasterOkcSicilNo;
        public string szUserDefinedTranGroup;
        public string szUserDefinedTranSubGroup;
        public UInt32 ReceiptAmount;
        public byte Status;

        public ST_HANDLE_LIST()
        {
            szMasterOkcSicilNo = "";
            szUserDefinedTranGroup = "";
            szUserDefinedTranSubGroup = "";
        }
    };

    public class promotion
    {
        public byte type { get; set; }
        public int amount { get; set; }
        public string? ticketMsg { get; set; }
    };

    public class ST_TICKET_HEADER
    {
        public string szMerchName1;
        public string szMerchName2;
        public string szMerchAddr1;
        public string szMerchAddr2;
        public string szMerchAddr3;
        public string VATOffice;
        public string VATNumber;
        public string MersisNo;
        public string TicariSicilNo;
        public string WebAddress;
        public int initDateTime;
        public short index;
        public short EJNo;

        public ST_TICKET_HEADER()
        {
            szMerchName1 = "";
            szMerchName2 = "";
            szMerchAddr1 = "";
            szMerchAddr2 = "";
            szMerchAddr3 = "";
            VATOffice = "";
            VATNumber = "";
            MersisNo = "";
            TicariSicilNo = "";
            WebAddress = "";
        }
    };

    /** ETransactionFlags */
    public enum ETransactionFlags
    {
        FLG_XTRANS_GMP3 = (1 << 1),
        FLG_XTRANS_FROM_FILE = (1 << 2),
        FLG_XTRANS_TAXFREE_PARAMETERS_SET = (1 << 8),
        FLG_XTRANS_TICKETTING_EXISTS = (1 << 9),
        FLG_XTRANS_FULL_RCPT_CANCEL = (1 << 12),
        FLG_XTRANS_NONEY_COLLECTION_EXISTS = (1 << 13),
        FLG_XTRANS_TAXLESS_ITEM_EXISTS = (1 << 14),
        FLG_XTRANS_INVOICE_PARAMETERS_SET = (1 << 15),
        FLG_XTRANS_TICKET_HEADER_PRINTED = (1 << 17),
        FLG_XTRANS_TICKET_TOTALS_AND_PAYMENTS_PRINTED = (1 << 18),
        FLG_XTRANS_TICKET_FOOTER_BEFORE_MF_PRINTED = (1 << 19),
        FLG_XTRANS_TICKET_FOOTER_MF_PRINTED = (1 << 20),
        FLG_XTRANS_ONLINE_INVOICE_PARAMETERS_SET = (1 << 21),
    };

    public enum EVasType
    {
        TLV_OKC_ASSIST_VAS_TYPE_ADISYON = 0x0001,   // Adisyon
        TLV_OKC_ASSIST_VAS_TYPE_IN_FLIGHT,              // INFLIGHT -->
        TLV_OKC_ASSIST_VAS_TYPE_INGENICO,               // ICIRO, FIND MY KASA
        TLV_OKC_ASSIST_VAS_TYPE_OTHER,                  // OTHER
        TLV_OKC_ASSIST_VAS_TYPE_AKTIFNOKTA,             // AKTIF NOKTA
        TLV_OKC_ASSIST_VAS_TYPE_MOBIL_ODEME,            // MOBIL ODEME
        TLV_OKC_ASSIST_VAS_TYPE_OTOPARK,             	// OTOPARK
        TLV_OKC_ASSIST_VAS_TYPE_YEMEKCEKI,             	// YEMEK KARTI
        TLV_OKC_ASSIST_VAS_TYPE_LOYALTY,             	// SADAKAT UYGULAMASI
        TLV_OKC_ASSIST_VAS_TYPE_PAYMENT,                // ODEME UYGULAMASI
        TLV_OKC_ASSIST_VAS_TYPE_ALL = 0x0100    // ALL
    };

    /** INFO Function type */
    public enum EInfo
    {
        TLV_FUNC_INFO_DEVICE = 0xEF10,     			   // 00 Info device
        TLV_FUNC_INFO_FISCAL,                          // 01 Info fiscal
        TLV_FUNC_INFO_FRAM,                            // 02 Info fram
        TLV_FUNC_INFO_DB,                              // 03 Info DB
        TLV_FUNC_INFO_DAILY,                           // 04 Info daily
        TLV_FUNC_INFO_EVENT,                           // 05 Info event
        TLV_FUNC_INFO_EKU,                             // 06 Info eku
    };

    public enum EItemOptions
    {
        ITEM_OPTION_TAX_EXCEPTION_TAXLESS = (1 << 12),
        ITEM_TAX_EXCEPTION_VAT_INCLUDED_TO_PRICE = (1 << 12),
        ITEM_TAX_EXCEPTION_VAT_NOT_INCLUDED_TO_PRICE = (1 << 15),
    };

    public enum EItemPromotionType
    {
        ITEM_PROMOTION_DISCOUNT = 1,
        ITEM_PROMOTION_INCREASE = 2,
    };

    public enum ECurrency
    {
        CURRENCY_NONE = 0,
        CURRENCY_TL = 949,
        CURRENCY_DOLAR = 840,
        CURRENCY_EU = 978,
        CURRENCY_GPR = 826,
    };

    public enum EPaymentTypes
    {
        PAYMENT_ALL = 0x000FFFFF,	            //  NAKIT  KREDI  OTHER  YCEKI  DOVIZ   MATRAH  MENU(ODEME TIPLERI)
        PAYMENT_CASH_TL = 0x00000001,	        // 	++++   xxxx   xxxx   xxxx   xxxx    ++++	xxxx
        PAYMENT_CASH_CURRENCY = 0x00000002,	    // 	xxxx   xxxx   xxxx   xxxx   ++++    ++++    xxxx
        PAYMENT_BANK_CARD = 0x00000004,	        //	xxxx   ++++   xxxx   xxxx   xxxx    ++++    xxxx
        PAYMENT_YEMEKCEKI = 0x00000008,	        //	xxxx   xxxx   xxxx   ++++   xxxx    xxxx    xxxx(Uygulama varsa)
        PAYMENT_MOBILE = 0x00000010,	        // 	xxxx   ++++   xxxx   xxxx   xxxx    ++++    xxxx(Uygulama varsa)
        PAYMENT_HEDIYE_CEKI = 0x00000020,       // 	xxxx   xxxx   ++++   xxxx   xxxx    ++++    ++++
        PAYMENT_IKRAM = 0x00000040,             // 	xxxx   xxxx   ++++   xxxx   xxxx    ++++    ++++
        PAYMENT_ODEMESIZ = 0x00000080,          // 	xxxx   xxxx   ++++   xxxx   xxxx    ++++    ++++
        PAYMENT_KAPORA = 0x00000100,            // 	xxxx   xxxx   ++++   xxxx   xxxx    ++++    ++++
        PAYMENT_PUAN = 0x00000200,              // 	xxxx   xxxx   ++++   xxxx   xxxx    ++++    ++++
        PAYMENT_GIDER_PUSULASI = 0x00000400,    // 	xxxx   xxxx   ++++   xxxx   xxxx    ++++    ++++
        PAYMENT_BANKA_TRANSFERI = 0x00000800,   // xxxx   xxxx   ++++   xxxx   xxxx    ++++    ++++
        PAYMENT_CEK = 0x00001000,               // 	xxxx   xxxx   ++++   xxxx   xxxx    ++++    ++++
        PAYMENT_ACIK_HESAP = 0x00002000,        // 	xxxx   xxxx   ++++   xxxx   xxxx    ++++    ++++
        PAYMENT_DIGER = 0x00004000,   // 	xxxx   xxxx   ++++   xxxx   xxxx    ++++    ++++

        //REVERSE_PAYMENT_ALL = 0xFFF00000,     //açılacak
        REVERSE_PAYMENT_CASH = 0x00100000,
        REVERSE_PAYMENT_BANK_CARD_VOID = 0x00200000,
        REVERSE_PAYMENT_BANK_CARD_REFUND = 0x00400000,
        REVERSE_PAYMENT_YEMEKCEKI = 0x00800000,
        REVERSE_PAYMENT_MOBILE = 0x01000000,
        REVERSE_PAYMENT_HEDIYE_CEKI = 0x02000000,
        REVERSE_PAYMENT_PUAN = 0x04000000,
        REVERSE_PAYMENT_ACIK_HESAP = 0x08000000,
        REVERSE_PAYMENT_KAPORA = 0x10000000,
        REVERSE_PAYMENT_GIDER_PUSULASI = 0x20000000,
        REVERSE_PAYMENT_BANKA_TRANSFERI = 0x40000000,

    };


    /** Sub types of the payment,refund for PAYMENT_TYPE_BANKING_CARD */
    public enum EPaymentSubtypes
    {
        PAYMENT_SUBTYPE_PROCESS_ON_POS = 0x00000000,
        PAYMENT_SUBTYPE_SALE = 0x00000001,
        PAYMENT_SUBTYPE_INSTALMENT_SALE = 0x00000002,
        PAYMENT_SUBTYPE_LOYALTY_PUAN = 0x00000003,
        PAYMENT_SUBTYPE_ADVANCE_REFUND = 0x00000004,
        PAYMENT_SUBTYPE_INSTALLMENT_REFUND = 0x00000005,
        PAYMENT_SUBTYPE_REFERENCED_REFUND = 0x00000006,
        PAYMENT_SUBTYPE_REFERENCED_REFUND_WITH_CARD = 0x00000007,
        PAYMENT_SUBTYPE_REFERENCED_REFUND_WITHOUT_CARD = 0x00000008,
    };



    /** Bank Aplication transaction flags*/
    public enum EBANK_APLICATION_TRANSACTION_FLAGS_t
    {

    };

    /** Sub types of the discount for Bank, VAS aplication or ticket discount process.*/
    public enum DiscountSubtypes
    {
        DICOUNT_SUBTYPE_BANKING_INDIRIM = 0x0001,
        DICOUNT_SUBTYPE_RECEIPT_INDIRIM = 0x0002,
        DICOUNT_SUBTYPE_BANKING_INDIRIM_MATRAHSIZ = 0x0003,
        DICOUNT_SUBTYPE_VAS_INDIRIM = 0x0004,
    };

    public enum EItemUnitTypes
    {
        ITEM_NONE,
        ITEM_NUMBER = 1,
        ITEM_KILOGRAM = 2,
        ITEM_GRAM = 3,
        ITEM_LITRE = 4,

        // Adetsel Birimler
        ITEM_DUZINE = 11,
        ITEM_DEMET, // 12
        ITEM_KASA, // 13
        ITEM_BAG, // 14

        // Aðýrlýk Birimler
        ITEM_MILIGRAM = 31,
        ITEM_TON, // 32
        ITEM_ONS, // 33
        ITEM_DESIGRAM, // 34
        ITEM_SANTIGRAM, // 35
        ITEM_POUND, // 36
        ITEM_KENTAL, // 37

        // Uzunluk Birimler
        ITEM_METRE = 51,
        ITEM_SANTIMETRE, // 52
        ITEM_MILIMETRE, // 53
        ITEM_DEKAMETRE, // 54
        ITEM_HEKTAMETRE, // 55
        ITEM_KILOMETRE, // 56
        ITEM_DESIMETRE, // 57
        ITEM_MIKRON, // 58
        ITEM_INC, // 59
        ITEM_FOOT, // 60
        ITEM_YARD, // 61
        ITEM_MIL, // 62

        // Hacim Birimler
        ITEM_METREKUP = 71,
        ITEM_DESIMETREKUP, // 72
        ITEM_SANTIMETREKUP, // 73
        ITEM_MILIMETREKUP, // 74
        ITEM_DEKALITRE, // 75
        ITEM_HEKTOLITRE, // 76
        ITEM_KILOLITRE, // 77
        ITEM_DESILITRE, // 78
        ITEM_SANTILITRE, // 79
        ITEM_MILILITRE, // 80
        ITEM_INCKUP, // 81
        ITEM_GALLON, // 82
        ITEM_BUSHEL, // 83

        // Alan Birimler
        ITEM_METREKARE = 91,
        ITEM_DEKAMETREKARE, // 92
        ITEM_AR, // 93
        ITEM_KILOMETREKARE, // 94
        ITEM_DESIMETREKARE, // 95
        ITEM_SANTIMETREKARE, // 96
        ITEM_MILIMETREKARE, // 97
        ITEM_DONUM, // 98
        ITEM_HEKTAR, // 99
        ITEM_INCKARE, // 100
    };

    public enum ETransactionFiscalType
    {
        TRANSACTION_FISCAL_TYPE_SALE = 0,
        TRANSACTION_FISCAL_TYPE_REFUND,
        TRANSACTION_FISCAL_TYPE_VOID,
        TRANSACTION_FISCAL_TYPE_NON_FISCAL_SALE,
        TRANSACTION_FISCAL_TYPE_INFO,
    };

    // FP3_Function için Flaglar
    enum FunctionFlags
    {
        GMP_EXT_DEVICE_FUNC_BIT_PARAM_YUKLE = 0x00000001,
        GMP_EXT_DEVICE_FUNC_BIT_Z_RAPOR, // 0x00000002                          
        GMP_EXT_DEVICE_FUNC_BIT_X_RAPOR, // 0x00000003
        GMP_EXT_DEVICE_FUNC_BIT_MALI_RAPOR, // 0x00000004
        GMP_EXT_DEVICE_FUNC_BIT_EKU_RAPOR, // 0x00000005
        GMP_EXT_DEVICE_FUNC_BIT_MALI_KUMULATIF, // 0x00000006
        GMP_EXT_DEVICE_FUNC_BIT_Z_RAPOR_GONDER, // 0x00000007
        GMP_EXT_DEVICE_FUNC_BIT_KASIYER_SEC, // 0x00000008
        GMP_EXT_DEVICE_FUNC_BIT_KASIYER_LOGOUT, // 0x00000009
        GMP_EXT_DEVICE_FUNC_BIT_AVANS, // 0x0000000A
        GMP_EXT_DEVICE_FUNC_BIT_ODEME, // 0x0000000B
        GMP_EXT_DEVICE_FUNC_BIT_CEKMECE_ACMA, // 0x0000000C
        GMP_EXT_DEVICE_FUNC_READ_CARD, // 0x0000000D
        GMP_EXT_DEVICE_FUNC_GET_CARD_PUAN, // 0x0000000E
        GMP_EXT_DEVICE_FUNC_BIT_BANKA_GUN_SONU, // 0x0000000F
        GMP_EXT_DEVICE_FUNC_BIT_BANKA_PARAM_YUKLE, // 0x00000010
        GMP_EXT_DEVICE_FUNC_BANKA_IADE, // 0x00000011
        GMP_EXT_DEVICE_FUNC_GET_UNIQUE_ID_LIST, // 0x00000012
        GMP_EXT_DEVICE_FUNC_EKU_RAPOR_SON_FIS_KOPYA, // 0x00000013
        GMP_EXT_DEVICE_FUNC_EKU_RAPOR_SON_KOPYA, // 0x00000014
        GMP_EXT_DEVICE_FUNC_EKU_RAPOR_IKI_Z_ARASI, // 0x00000015
        GMP_EXT_DEVICE_FUNC_EKU_RAPOR_IKI_TARIH_ARASI, // 0x00000016
        GMP_EXT_DEVICE_FUNC_EKU_RAPOR_FISTEN_FISE, // 0x00000017
        GMP_EXT_DEVICE_FUNC_EKU_RAPOR_Z_KOPYA, // 0x00000018
        GMP_EXT_DEVICE_FUNC_EKU_RAPOR_DETAIL, // 0x00000019
        GMP_EXT_DEVICE_FUNC_EKU_RAPOR_SUMMARY, // 0x0000001A
        GMP_EXT_DEVICE_FUNC_CHANGE_RECEIPT_HEADER, // 0x0000001B
        GMP_EXT_DEVICE_FUNC_BANKA_IPTAL, // 0x0000001C
        GMP_EXT_DEVICE_FUNC_KASIYER_TANIMLA, // 0x0000001D		/**< used for define cashier */
        GMP_EXT_DEVICE_FUNC_TRANS_INQUERY, // 0x0000001E		/**< used for transaction inquery */
        GMP_EXT_DEVICE_FUNC_TRANS_STATUS, // 0x0000001F
        GMP_EXT_DEVICE_FUNC_CREATE_UNIQUE_ID, // 0x00000020
        GMP_EXT_DEVICE_FUNC_LOAD_BACKGROUND_TO_FRONT, // 0x00000021
        GMP_EXT_DEVICE_FUNC_LOYALTY_IDENTIFICATION, // 0x00000022
        GMP_EXT_DEVICE_FUNC_GET_INVOICE_INFO, // 0x00000023
        GMP_EXT_DEVICE_FUNC_PAYMENT_VAS_IPTAL, // 0x00000024
        GMP_EXT_DEVICE_FUNC_CHECK_PAYMENT_STATUS, // 0x00000025
        GMP_EXT_DEVICE_FUNC_BIT_AYLIK_RAPOR_GONDER // = 0x000000026
    };

    public enum TTicketType
    {
        TTasnifDisi = 0,
        TProcessSale = 1,       //Fiscal Ticket           
        TZReport = 2,
        TXReport = 3,
        TEJReport = 4,
        TFiscal2Z = 5,
        TFiscal2T = 6,
        TFiscalCumm = 7,
        TAvans = 8,             //Non_Fiscal Ticket
        TPayment = 9,           //Non_Fiscal Ticket
        TZDonemReport = 10,
        TXDonemReport = 11,
        TXPluSale = 12,
        TInvoice = 13,          //Non_Fiscal Ticket
        TVoidSale = 14,         //Non_Fiscal Ticket
        TRefund = 15,           //Non_Fiscal Ticket
        TYemekceki = 16,        //Non_Fiscal Ticket
        TOtopark = 17,          //Non_Fiscal Ticket 
        TZReportForce = 18,
        TInfo = 19,             //Non_Fiscal Ticket
        TTaxFree = 20,          //Fiscal Ticket
        TDailyMemory = 21,
        TKasaAvans = 22,        //Non_Fiscal Ticket
        TCariHesap,						//Non_Fiscal Ticket
        TUniqueId = 127,
        TLAST              // Bu satir hep sonda kalmali
    };

    public enum EKU_STATUS_t
    {
        EKU_STATUS_VIRGIN,
        EKU_STATUS_ACTIVE,
        EKU_STATUS_CLOSED,
        EKU_STATUS_UNDEFINED
    };

    public enum EInvoiceFlags
    {
        INVOICE_FLAG_IRSALIYE = 0x00000001,
    };


    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi), Serializable]
    public struct ST_TAX_RATE
    {
        [MarshalAs(UnmanagedType.U2)]
        public UInt16 taxRate;
    };


    public class ST_DEPARTMENT
    {
        public string szDeptName { get; set; }
        public byte u8TaxIndex { get; set; }
        public ECurrency iCurrencyType { get; set; }
        public EItemUnitTypes iUnitType { get; set; }
        public UInt64 u64Limit { get; set; }
        public UInt64 u64Price { get; set; }
        public byte bLuchVoucher { get; set; }

        public ST_DEPARTMENT()
        {
            szDeptName = "";
        }
    };

    public class ST_GLOBAL_XML_DATA
    {
        public byte IsCheckStructVersion;
        public int LogFileSize;
        public string LogPath;
    }

    public struct ST_DATE
    {
        public byte day;		// 1-31
        public byte month;		// 1-12
        public UInt16 year;		// 0-99
    };

    public struct ST_TIME
    {
        public byte hour;		// 0-23
        public byte minute;		// 0-59
        public byte second;		// 0-59
    };

    public struct ST_FUNCTION_PARAMETERS_PASSWORD
    {
        public string supervisor;
        public string cashier;
    };

    public struct ST_FUNCTION_PARAMETERS_POINT
    {
        public UInt32 ZNo;
        public UInt32 FNo;
        public ST_DATE date;
        public ST_TIME time;
    };

    public struct ST_FUNCTION_PARAMETERS
    {
        public UInt32 EKUNo;
        public ST_FUNCTION_PARAMETERS_PASSWORD Password;
        public ST_FUNCTION_PARAMETERS_POINT start;
        public ST_FUNCTION_PARAMETERS_POINT finish;
    };

    public class ST_CASHIER
    {
        public UInt16 index;
        public UInt32 flags;
        public string name;

        public ST_CASHIER()
        {
            name = "";
        }
    };

    public class ST_INI_PARAM
    {
        public byte IsCheckStructVersion;
        public byte RetryCounter;
        public byte IpRetryCount;
        public UInt32 AckTimeOut;
        public UInt32 CommTimeOut;
        public UInt32 InterCharacterTimeOut;

        public string PortName;
        public UInt32 BaudRate;
        public byte ByteSize;
        public byte fParity;
        public byte Parity;
        public byte StopBit;
        public byte IsTcpConnection;
        public string IP;
        public UInt32 Port;

        public string LogPath;
        public byte LogPrintToFileOpen;
        public byte LogPrintToConsoleOpen;
        public byte LogGeneralOpen;
        public byte LogFunctionOpen;
        public byte LogSecurityOpen;
        public byte LogPrivateSecurityOpen;
        public byte LogCommOpen;
        public byte LogExtDevOpen;
        public byte LogJsonOpen;
        public byte LogJsonDataOpen;
        public byte LogGmp3TagsOpen;
        public byte LogPrintSerialNumOpen;
        public byte LogPrintDateOpen;
        public byte LogPrintTimeOpen;
        public byte LogPrintTypeOpen;
        public byte LogPrintVersionOpen;
        public byte LogPrintSourceFileOpen;
        public byte LogPrintSourceLineOpen;

        public ST_INI_PARAM()
        {
            PortName = "";
            IP = "";
            LogPath = "";
        }
    };

    public class ST_CARD_INFO
    {
        public byte inputType;
        public string pan;
        public string holderName;
        public byte[] type;

        public ST_CARD_INFO()
        {
            inputType = new byte();
            pan = "";
            holderName = "";
            type = new byte[3];
        }
    };


    public class ST_USER_MESSAGE
    {
        public UInt32 flag;
        public UInt16 len;
        public string message;

        public ST_USER_MESSAGE()
        {
            message = "";
        }
    };

    //public class ST_USER_MESSAGE_EX
    //{
    //    public UInt32 flag;
    //    public int len;
    //    public string message;

    //    public ST_USER_MESSAGE_EX()
    //    {
    //        message = "";
    //    }
    //};

    public class ST_PAYMENT_APPLICATION_INFO
    {
        public byte[] name;
        public byte index;
        public UInt16 u16BKMId;
        public UInt16 Status;
        public UInt16 Priority;
        public UInt16 u16AppId;
        public UInt16 AppType;
        public UInt16 AppFlag;

        public ST_PAYMENT_APPLICATION_INFO()
        {
            name = new byte[20];
        }
    } ;

    public struct ST_PLU_RECORD
    {
        public byte deptIndex;
        public byte unitType;
        public UInt32 flag;
        public UInt32 lastModified;
        public UInt16[] currency;
        public UInt32[] amount;
        public string barcode;
        public string name;
        public UInt32 groupParentId;
    };

    public class ST_LOYALTY_SERVICE_INFO
    {
        public byte[] name;						/**< Name of the Service */
        public string CustomerId;				/**< Customer ID*/
        public UInt16 ServiceId;				/**< Service ID*/
        public UInt16 u16AppId;				    /**< VAS app ID*/
        public UInt16 CustomerIdType;			/**< Type of entry: MOB:1, CUSTOMER ID:2, OTHER:3 */
        public UInt32 TotalDiscountAmount;

        public ST_LOYALTY_SERVICE_INFO()
        {
            name = new byte[24];
            CustomerId = "";
        }
    };

    public class ST_LOYALTY_SERVICE_REQ
    {
        public byte[] name;						/**< Name of the Service */
        public string CustomerId;				/**< Customer ID*/
        public UInt16 ServiceId;				/**< Service ID*/
        public UInt16 u16AppId;				    /**< VAS app ID*/
        public UInt16 CustomerIdType;			/**< Type of entry: MOB:1, CUSTOMER ID:2, OTHER:3 */
        public UInt32 Amount;					/**< Amount*/
        public byte[] rawData;					/**< 512 byte buffer to transmit or receive data to/from loyaltu application*/
        public UInt16 rawDataLen;				/**< Raw data length */

        public ST_LOYALTY_SERVICE_REQ()
        {
            name = new byte[24];
            rawData = new byte[512];
            CustomerId = "";
        }
    };

    public struct ST_PLU_GROUP_RECORD
    {
        public UInt32 groupId;
        public UInt32 groupFlag;
        public string name;
    };

    public class ST_INVIOCE_INFO
    {
        public byte source;
        public byte[] no;
        public byte[] date;
        public byte[] tck_no;
        public byte[] vk_no;
        public UInt64 amount;
        public UInt16 currency;
        public UInt32 flag;

        public ST_INVIOCE_INFO()
        {
            no = new byte[25];
            date = new byte[3];
            tck_no = new byte[12];
            vk_no = new byte[12];
        }
    };

    public class ST_ONLINE_INVIOCE_INFO
    {
        public string CustomerName;
        public string VKN;
        public string HomeAddress;
        public string District;
        public string City;
        public string Country;
        public string Mail;
        public string WebSite;
        public string Phone;
        public string TaxOffice;
        public string Ettn;
        public string DespatchNo;
        public string Identifier;
        public string OrderNo;

        public byte[] Type;
        public byte[] OrderDate;
        public byte[] DespatchDate;
        public string SellerIdentifier_OnlineInvoice;
        public string SellerIdentifier_OnlineArchive;
        public UInt16 rawDataLen;
        public byte[] rawData;


        public ST_ONLINE_INVIOCE_INFO()
        {
            CustomerName = "";
            VKN = "";
            HomeAddress = "";
            District = "";
            City = "";
            Country = "";
            Mail = "";
            WebSite = "";
            Phone = "";
            TaxOffice = "";
            Ettn = "";
            DespatchNo = "";
            Identifier = "";
            OrderNo = "";
            Type = new byte[2];
            OrderDate = new byte[7];
            DespatchDate = new byte[7];
            SellerIdentifier_OnlineArchive = "";
            SellerIdentifier_OnlineInvoice = "";
            rawDataLen = 0;
            rawData = new byte[512];
        }
    };

    public class ST_TAXFREE_INFO
    {
        public string BuyerName;
        public string BuyerSurname;
        public string VKN;
        public byte[] IDDate;
        public string City;
        public string Country;
        public string CountryCode;
        public string Identifier;
        public string Ettn;

        public ST_TAXFREE_INFO()
        {
            BuyerName = "";
            BuyerSurname = "";
            VKN = "";
            IDDate = new byte[3];
            City = "";
            Country = "";
            CountryCode = "";
            Identifier = "";
            Ettn = "";
        }
    };

    public struct ST_FILE
    {
        //[MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
        //public byte[] fileName;
        public string fileName;
        public int fileSize;
    };

    public class ST_UNIQUE_ID
    {
        public byte[] uniqueId;
        public UInt16 reserved1;
        public UInt32 reserved2;

        public ST_UNIQUE_ID()
        {
            uniqueId = new byte[24];
        }
    };

    // EKU record information
    public struct EKU_RECORD_t
    {
        public UInt32 DateTime;
        public UInt32 Amount;
        public UInt32 Vat;
        public UInt16 FNo;
        public UInt16 ZNo;
        public UInt16 Type;                                                              // 2 bytes for alignment, 1 byte written to FLASH
        public UInt16 Status;
    };

    // Eku info
    public struct EKU_INFO_t
    {
        public EKU_RECORD_t LastRecord;
        public UInt32 MapFreeArea;				   	// TLV_PARAM_INFO_EKU_MAP_FREE
        public UInt32 MapUsedArea;				   	// TLV_PARAM_INFO_EKU_MAP_USED
        public UInt32 DataFreeArea;				    // TLV_PARAM_INFO_EKU_DATA_FREE
        public UInt32 DataUsedArea;				    // TLV_PARAM_INFO_EKU_DATA_USED
        public UInt16 HeaderUsed;				    // TLV_PARAM_INFO_EKU_HEADER_USED
        public UInt16 HeaderTotal;				    // TLV_PARAM_INFO_EKU_HEADER_TOTAL
        public EKU_STATUS_t Status;                 // TLV_FUNC_INFO_EKU
        public UInt16 CpuCRC;
    };

    public struct FISCAL_INTEGRITY_t
    {
        public byte Fiscal;
        public byte Event;
        public byte Daily;
        public byte RFU;
    };


    public struct MEMORY_INFO_t
    {
        public byte[] ID;
        public UInt16 Size;
    };


    public class ST_MODULE_USAGE_INFO
    {
        public string szHardwareReference = "";
        public string szHardwareSerial = "";
        public UInt32 MapFreeArea;
        public UInt32 MapUsedArea;
        public UInt32 DataFreeArea;
        public UInt32 DataUsedArea;
    };

    public class DEVICE_INFO_t
    {
        public string szSoftVersion = "";
        public string szHardVersion = "";
        public string szCompileDate = "";
        public string szDescription = "";
        public string szHardwareReference = "";
        public string szHardwareSerial = "";
        public string szCpuID = "";
        public string szHash = "";
        public string szBootVersion = "";
        public FISCAL_INTEGRITY_t Integrity;
        public MEMORY_INFO_t Flash1;
        public MEMORY_INFO_t Flash2;
        public MEMORY_INFO_t Fram;
        public UInt16 CpuCRC;
        public byte Authentication;
    };

    public class ST_EKU_MODULE_INFO
    {
        public DEVICE_INFO_t Device = new DEVICE_INFO_t();
        public EKU_INFO_t Eku = new EKU_INFO_t();
    };

    // Init close structure
    public struct EKU_INI_CLS_t
    {
        public UInt32 DateTime;
        public UInt16 ZNo;
        public UInt16 FNo;
    };

    // Eku header
    public struct ST_EKU_HEADER
    {
        public byte[] SicilNo;
        public byte[] TerminalSerialNo;
        public byte[] TerminalProductCode;
        public byte[] SoftwareVersion;
        public byte[] MerchantName;
        public byte[] MerchantAddress;
        public byte[] VATOffice;
        public byte[] VATNumber;
        public EKU_INI_CLS_t Init;
        public EKU_INI_CLS_t Close;
        public UInt16 Active;
        public UInt16 EkuCount;
        public UInt16 HeaderIndex;
        public UInt16 HeaderTotal;
        public byte[] MersisNo;
        public byte[] TicariSicilNo;
        public byte[] WebAddress;
        public byte[] ApplicationUse;
    };


    public class ST_EKU_APPINF
    {
        public byte[] Buffer;
        public UInt32 Amount;
        public UInt32 Vat;
        public byte[] DateTime;		//YMDHMS
        public byte[] DateTimeDelta;	//YMDHMS
        public UInt16 BufLen;
        public UInt16 RecLen;
        public UInt16 RemLen;
        public UInt16 ZNo;
        public UInt16 FNo;
        public UInt16 Type;
        public UInt16 Func;
        public UInt16 DateTimeCount;
        public UInt16 RecordStatus;

        public ST_EKU_APPINF()
        {
            Buffer = new byte[1024];
            DateTime = new byte[6];
            DateTimeDelta = new byte[6];
        }
    };

    public class ST_TRANS_INQUIRY
    {
	    public UInt16 BankBkmId;
	    public string szTerminalId;
	    public UInt32 Batch;
	    public UInt32 Stan;
	    public byte TransactionType;
	    public UInt16 ECROptions;
	    public UInt32 ECROptions2;
	    public UInt16 MessageResponseCode;
	    public UInt16 AuthorisedBank;
        public string szResponseCode;
        public string szTransactionDateTime;
	    public UInt16 TransactionInformationFlags;
	    public UInt16 ApplicationInformationFlags;
	    public string szPAN;
    	public UInt32 AuthorisedAmount;
	    public string szAuthorisationNumber;
	    public string szBankHostResponseCode;
	    public string szAdditionalResponseDescriptionForDisplay;
	    public string szBankApplicationSpecificInternalErrorDescription;
	    public UInt16 BankSpecificErrorCode;
	    public string szPOSApplicationBankVersion;
	    public string szPOSApplicationInternalVersion;

        public ST_TRANS_INQUIRY()
        {
	        szTerminalId = "";
            szResponseCode = "";
            szTransactionDateTime = "";
            szPAN = "";
            szAuthorisationNumber = "";
            szBankHostResponseCode = "";
            szAdditionalResponseDescriptionForDisplay = "";
            szBankApplicationSpecificInternalErrorDescription = "";
            szPOSApplicationBankVersion = "";
            szPOSApplicationInternalVersion = "";
        }
    };

    public class ST_PAYMENT_CHECK_RESPONSE
    {
        public string odemeOnayKod;
        public string refundRNN;
        public byte[] uniqueId;
        public byte TaksitSayisi;
        public string CardHolderName;
        public byte ReaderTypes;
        public byte [] CardType;
        public UInt32 Tutar;
        public UInt16 BankName;
        public UInt16 BKMIdU16;
        public UInt32 BatchNo;
        public UInt32 STAN;
        public string TerminalId;
        public string MerchantId;
        public byte ProvisionId;
        public UInt16 BKMId;

        public ST_PAYMENT_CHECK_RESPONSE()
        {
            CardType = new byte[3];
        }
    };

    /** EConditionIds */
    public enum EConditionIds
    {
        GMP3_CONTITION_ID_PAYMENT_TOTAL_AMOUNT = 1,	/**< UINT32, Total Payment Amount value */
        GMP3_CONTITION_ID_IS_TICKET_PAYMENT_COMPLETED,		/**< BOOLEAN, Is All Ticket Payment completed */
    };

    /** EConditionTest */
    public enum EConditionTest
    {
        EIsEqual = 0,			        /**< "==" */
        EIsBigger,				        /**< ">" */
        EIsEqualOrBigger,				/**< "=>" */
        EIsSmaller,				        /**< "<" */
        EIsEqualOrSmaller,				/**< "<=" */
    };

    /** Subtype of the MATRAHSIZ transaction in TaxExceptioan  */
    public enum ETypeOfMatrahsiz
    {
        MATRAHSIZ_TYPE_ILAC = 1,
        MATRAHSIZ_TYPE_MUAYANE,
        MATRAHSIZ_TYPE_MUAYANE_RECETE,
        MATRAHSIZ_TYPE_INVOICE_COLLECTION,
    };

    /** Structure which is used to define a conditional case */
    public struct ST_CONDITIONAL_IF
    {
        public EConditionIds id;				    /**< One of EConditionIds */
        public EConditionTest eTestFormule;			/**< One of EConditionTest */
        public ulong ui64TestValue;					/**< A value to test  */
        public ushort IfTrue_GOTO;					/**< One of EConditionGoto OR index on subCommands list */
        public ushort IfFalse_GOTO;					/**< One of EConditionGoto OR index on subCommands list */
    };

    public struct ST_DATE_TIME
    {
        public UInt16 year;		    /**< Year (1900..2100)*/
        public byte month;		    /**< Month (1..12)*/
        public byte day;			/**< Day (1..31)*/
        public byte hour;			/**< Hour (0..23)*/
        public byte minute;		    /**< Minute (0..59)*/
        public byte second;		    /**< Second (0..59)*/
    } ;

    public class ST_MULTIPLE_RETURN_CODE
    {
        public UInt32 subCommand;						/**< subCommand which this result is produced for. If it is zero, then there is no subCommand and the data is produced automaticly by uApplication */
        public UInt32 retcode;							/**< subCommand return code (result of the subCommand on ECR) */
        public UInt32 tag;								/**< tag value provided by External Application (or one of GMP tag if it is automatic response from uApplcation) to mark the output data */
        public UInt16 indexOfSubCommand;				/**< order of the subCommand in the request message package. It is started by one and increased in each subcommand. if it is zero then there is no subCommand and the response is automatic from uApplication */
        public UInt16 lengthOfData;					/**< [IN] Maximum data buffer size [OUT] Length of the subCommand data returned*/
        public byte[] pData;							/**< pointer to the returned data of the subCommand. It must be allocated by External Application. If it is NULL, data will not be copied even if returned from ECR */

        public ST_MULTIPLE_RETURN_CODE()
        {
            pData = new byte[100];
        }
    } ;


    public class ST_DATABASE_COLOMN
    {
        public int typeOfData;
        public string data;
    };

    public class ST_DATABASE_LINE
    {
        public int indexOfLine;
        public int numberOfColomns;
        public ST_DATABASE_COLOMN[] pstColomnArray;

        public ST_DATABASE_LINE()
        {
            pstColomnArray = new ST_DATABASE_COLOMN[50];
        }
    };

    public class ST_DATABASE_RESULT
    {
        public int numberOfLines;
        public ST_DATABASE_LINE[] pstCaptionArray;
        public ST_DATABASE_LINE[] pstLineArray;

        public ST_DATABASE_RESULT()
        {
            pstCaptionArray = new ST_DATABASE_LINE[50];
            pstLineArray = new ST_DATABASE_LINE[50];
        }
    };

    public class ST_MULTIPLE_BANK_RESPONSE
    {
        public short bkmId;
        public short rescode;
    };

    class GMP_Tools
    {
        public static string SetEncoding(byte[] arr)
        {
            return Encoding.GetEncoding(65001).GetString(arr);
            //return Encoding.GetEncoding("iso-8859-9").GetString(arr);
        }

        public static string SetEncoding(byte[] arr, int index, int len)
        {
            return Encoding.GetEncoding("iso-8859-9").GetString(arr, index, len);
        }

        public static byte[] GetBytesFromString(string str)
        {
            byte[] Result = new byte[str.Length + 1];
            int Index = 0;
            foreach (var i in str)
            {
                if (i == 'Ğ')
                    Result[Index] = 0xD0;
                else if (i == 'Ü')
                    Result[Index] = 0xDC;
                else if (i == 'Ş')
                    Result[Index] = 0xDE;
                else if (i == 'İ')
                    Result[Index] = 0xDD;
                else if (i == 'Ö')
                    Result[Index] = 0xD6;
                else if (i == 'Ç')
                    Result[Index] = 0xC7;
                else if (i == 'I')
                    Result[Index] = 0x49;
                else if (i == 'ğ')
                    Result[Index] = 0xF0;
                else if (i == 'ü')
                    Result[Index] = 0xFC;
                else if (i == 'ş')
                    Result[Index] = 0xFE;
                else if (i == 'i')
                    Result[Index] = 0x69;
                else if (i == 'ö')
                    Result[Index] = 0xF6;
                else if (i == 'ç')
                    Result[Index] = 0xE7;
                else if (i == 'ı')
                    Result[Index] = 0xFD;
                else if (i == '€')
                    Result[Index] = 0x80;
                else
                    Result[Index] = Convert.ToByte(i);
                ++Index;
            }
            Result[Index] = 0x00;
            return Result;
        }

        public static string GetStringFromBytes(byte[] byt)
        {
            string Result = "";

            for (int i = 0; byt[i] != 0x00; i++)
            {
                if (i == byt.Length)
                    break;

                if (byt[i] == 0xD0)
                    Result += 'Ğ';
                else if (byt[i] == 0xDC)
                    Result += 'Ü';
                else if (byt[i] == 0xDE)
                    Result += 'Ş';
                else if (byt[i] == 0xDD)
                    Result += 'İ';
                else if (byt[i] == 0xD6)
                    Result += 'Ö';
                else if (byt[i] == 0xC7)
                    Result += 'Ç';
                else if (byt[i] == 0x49)
                    Result += 'I';
                else if (byt[i] == 0xF0)
                    Result += 'ğ';
                else if (byt[i] == 0xFC)
                    Result += 'ü';
                else if (byt[i] == 0xFE)
                    Result += 'ş';
                else if (byt[i] == 0x69)
                    Result += 'i';
                else if (byt[i] == 0xF6)
                    Result += 'ö';
                else if (byt[i] == 0xE7)
                    Result += 'ç';
                else if (byt[i] == 0xFD)
                    Result += 'ı';
                else if (byt[i] == 0x80)
                    Result += '€';
                else
                    Result += (char)byt[i];
            }
            return Result;
        }
    }

    class Json_GMPSmartDLL
    {
        [DllImport("GmpSmartDLL.dll", EntryPoint = "Json_FP3_CreateInterface", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 Json_FP3_CreateInterface(ref UInt32 phInt, byte[] szID, byte IsDefault, byte[] szJsonXmlData);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "Json_FP3_GetInterfaceXmlDataByID", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 Json_FP3_GetInterfaceXmlDataByID(byte[] szID, byte[] szInterfaceXmlData, int JsonMaxLen);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "Json_FP3_GetInterfaceXmlDataByHandle", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 Json_FP3_GetInterfaceXmlDataByHandle(UInt32 hInt, byte[] szInterfaceXmlData, int JsonMaxLen);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "Json_FP3_UpdateInterfaceXmlDataByID", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 Json_FP3_UpdateInterfaceXmlDataByID(byte[] szID, byte[] szInterfaceXmlData);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "Json_FP3_UpdateInterfaceXmlDataByHandle", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 Json_FP3_UpdateInterfaceXmlDataByHandle(UInt32 hInt, byte[] szInterfaceXmlData);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "Json_FP3_GetGlobalXmlData", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 Json_FP3_GetGlobalXmlData(byte[] szGlobalXmlData, int JsonMaxLen);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "Json_FP3_UpdateGlobalXmlData", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 Json_FP3_UpdateGlobalXmlData(byte[] szGlobalXmlData);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "Json_FP3_GetTaxRates", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 Json_FP3_GetTaxRates(UInt32 hInt, ref int pNumberOfTotalRecords, ref int pNumberOfTotalRecordsReceived, byte[] pJsonTaxRate, byte[] szJsonTaxRate_Out, int JsonTaxRateLen_Out, int NumberOfRecordsRequested);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "Json_FP3_GetDepartments", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 Json_FP3_GetDepartments(UInt32 hInt, ref int pNumberOfTotalDepartments, ref int pNumberOfTotalDepartmentsReceived, byte[] pJsonDepartments, byte[] szJsonDepartments_Out, int JsonDepartmentsLen_Out, int NumberOfDepartmentRequested);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "Json_FP3_GetTaxRates_Ex", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 Json_FP3_GetTaxRates_Ex(UInt32 hInt, byte offsetOfTaxRates, ref int pNumberOfTotalRecords, ref int pNumberOfTotalRecordsReceived, byte[] pJsonTaxRate, byte[] szJsonTaxRate_Out, int JsonTaxRateLen_Out, int NumberOfRecordsRequested);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "Json_FP3_GetDepartments_Ex", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 Json_FP3_GetDepartments_Ex(UInt32 hInt, byte offsetOfDepartments, ref int pNumberOfTotalDepartments, ref int pNumberOfTotalDepartmentsReceived, byte[] pJsonDepartments, byte[] szJsonDepartments_Out, int JsonDepartmentsLen_Out, int NumberOfDepartmentRequested);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "Json_FP3_GetCurrencyProfile", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 Json_FP3_GetCurrencyProfile(UInt32 hInt, byte [] szJsonExchangeProfileTable_In, byte [] szJsonExchangeProfileTable_Out, int szJsonExchangeProfileTable_Out_Length);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "Json_FP3_SetCurrencyProfile", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 Json_FP3_SetCurrencyProfile(UInt32 hInt, byte[] supervisorPassword, byte profileIndex, byte ProfileProcessType, byte[] szJsonExchangeProfileTable_In);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "Json_FP3_SetCurrencyProfileIndex", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 Json_FP3_SetCurrencyProfileIndex(UInt32 hInt, UInt64 hTrx, byte index, byte[] pstTicket, int Timeout);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "Json_FP3_SetDepartments", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 Json_FP3_SetDepartments(UInt32 hInt, byte[] pJsonDepartments, byte[] pJsonDepartments_Out, int pJsonDepartmentsLen_Out, byte NumberOfDepartmentRequested, byte[] szSupervisorPassword);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "Json_FP3_KasaAvans", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 Json_FP3_KasaAvans(UInt32 hInt, UInt64 hTrx, int Amount, byte[] szJsonTicket_Out, int JsonTicketLen_Out, int TimeoutInMiliseconds);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "Json_FP3_CustomerAvans", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 Json_FP3_CustomerAvans(UInt32 hInt, UInt64 hTrx, int Amount, byte[] szJsonTicket_Out, int JsonTicketLen_Out, byte[] szCustomerName, byte[] szTckn, byte[] szVkn, int TimeoutInMiliseconds);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "Json_FP3_CariHesap", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 Json_FP3_CariHesap(UInt32 hInt, UInt64 hTrx, int Amount, byte[] szJsonTicket_Out, int JsonTicketLen_Out, byte[] szCustomerName, byte[] szTckn, byte[] szVkn, byte[] szBelgeNo, byte[] szBelgeDate, int TimeoutInMiliseconds);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "Json_FP3_KasaPayment", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 Json_FP3_KasaPayment(UInt32 hInt, UInt64 hTrx, int Amount, byte[] szJsonTicket_Out, int JsonTicketLen_Out, int TimeoutInMiliseconds);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "Json_FP3_GetTicket", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 Json_FP3_GetTicket(UInt32 hInt, UInt64 hTrx, byte[] szJsonTicket_Out, int JsonTicketLen_Out, int TimeoutInMiliseconds);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "Json_FP3_ItemSale", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 Json_FP3_ItemSale(UInt32 hInt, UInt64 hTrx, byte[] szJsonItem, byte[] szJsonItem_Out, int JsonItemLen_Out, byte[] szJsonTicket_Out, int JsonTicketLen_Out, int TimeoutInMiliseconds);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "Json_FP3_Payment", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 Json_FP3_Payment(UInt32 hInt, UInt64 hTrx, byte[] stPaymentRequest, byte[] Out_stPaymentRequest, int Out_stPaymentRequestLen, byte[] szJsonTicket_Out, int JsonTicketLen_Out, int TimeoutInMiliseconds);//TIMEOUT_CARD_TRANSACTIONS

        [DllImport("GmpSmartDLL.dll", EntryPoint = "Json_FP3_FunctionVasPaymentRefund", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 Json_FP3_FunctionVasPaymentRefund(UInt32 hInt, byte[] stPaymentRequest, byte[] Out_stPaymentRequest, int Out_stPaymentRequestLen, int TimeoutInMiliseconds);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "Json_FP3_PrintUserMessage", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 Json_FP3_PrintUserMessage(UInt32 hInt, UInt64 hTrx, byte[] szJsonUserMessage, byte[] szJsonUserMessage_Out, int JsonUserMessageLen_Out, UInt16 NumberOfMessage, byte[] szJsonTicket_Out, int JsonTicketLen_Out, int TimeoutInMiliseconds);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "Json_FP3_PrintUserMessage_Ex", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 Json_FP3_PrintUserMessage_Ex(UInt32 hInt, UInt64 hTrx, byte[] szJsonUserMessage, byte[] szJsonUserMessage_Out, int JsonUserMessageLen_Out, UInt16 NumberOfMessage, byte[] szJsonTicket_Out, int JsonTicketLen_Out, int TimeoutInMiliseconds);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "Json_FP3_Plus", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 Json_FP3_Plus(UInt32 hInt, UInt64 hTrx, int Amount, byte[] szText, byte[] szJsonTicket_Out, int JsonTicketLen_Out, UInt16 IndexOfItem, int TimeoutInMiliseconds);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "Json_FP3_LoyaltyDiscount", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 Json_FP3_LoyaltyDiscount(UInt32 hInt, UInt64 hTrx, byte isRate, int Amount, byte Rate, byte[] szLoyaltyCustomerId, byte[] szText, UInt16 indexOfItem, ref  int pchangedAmount, byte[] szJsonTicket_Out, int JsonTicketLen_Out, int timeoutInMiliseconds);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "Json_FP3_Minus", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 Json_FP3_Minus(UInt32 hInt, UInt64 hTrx, int Amount, byte[] szText, byte[] szJsonTicket_Out, int JsonTicketLen_Out, UInt16 IndexOfItem, int TimeoutInMiliseconds);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "Json_FP3_Inc", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 Json_FP3_Inc(UInt32 hInt, UInt64 hTrx, byte Rate, byte[] szText, byte[] szJsonTicket_Out, int JsonTicketLen_Out, UInt16 IndexOfItem, ref  int pChangedAmount, int TimeoutInMiliseconds);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "Json_FP3_Dec", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 Json_FP3_Dec(UInt32 hInt, UInt64 hTrx, byte Rate, byte[] szText, byte[] szJsonTicket_Out, int JsonTicketLen_Out, UInt16 IndexOfItem, ref  int pChangedAmount, int TimeoutInMiliseconds);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "Json_FP3_VoidAll", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 Json_FP3_VoidAll(UInt32 hInt, UInt64 hTrx, byte[] szJsonTicket_Out, int JsonTicketLen_Out, int TmeoutInMiliseconds);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "Json_FP3_Pretotal", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 Json_FP3_Pretotal(UInt32 hInt, UInt64 hTrx, byte[] szJsonTicket_Out, int JsonTicketLen_Out, int TimeoutInMiliseconds);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "Json_FP3_Matrahsiz", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 Json_FP3_Matrahsiz(UInt32 hInt, UInt64 hTrx, byte[] TckNo, ushort SubtypeOfTaxException, int MatrahsizAmount, byte[] szJsonTicket_Out, int JsonTicketLen_Out, int TimeoutInMiliseconds);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "Json_FP3_VoidPayment", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 Json_FP3_VoidPayment(UInt32 hInt, UInt64 hTrx, UInt16 Index, byte[] szJsonTicket_Out, int JsonTicketLen_Out, int TimeoutInMiliseconds); // TIMEOUT_CARD_TRANSACTIONS

        [DllImport("GmpSmartDLL.dll", EntryPoint = "Json_FP3_VoidItem", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 Json_FP3_VoidItem(UInt32 hInt, UInt64 hTrx, UInt16 Index, UInt64 ItemCount, byte ItemCountPrecision, byte[] szJsonTicket_Out, int JsonTicketLen_Out, int TimeoutInMiliseconds);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "Json_FP3_FunctionGetUniqueIdList", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 Json_FP3_FunctionGetUniqueIdList(UInt32 hInt, byte[] szUniqueIdList, byte[] szUniqueIdList_Out, int UniqueIdListLen_Out, UInt16 MaxNumberOfitems, UInt16 IndexOfitemsToStart, ref UInt16 pTotalNumberOfItems, ref UInt16 pNumberOfItemsInThis, int TimeoutInMiliseconds);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "Json_FP3_FunctionReadCard", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 Json_FP3_FunctionReadCard(UInt32 hInt, int CardReaderTypes, byte[] szCardInfo, byte[] szCardInfo_Out, int CardInfoLen_Out, int TimeoutInMiliseconds);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "Json_FP3_GetCashierTable", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 Json_FP3_GetCashierTable(UInt32 hInt, ref int pNumberOfTotalRecords, ref int pNumberOfTotalRecordsReceived, byte[] szCashier, byte[] szCashier_Out, int CashierLen_Out, int NumberOfRecordsRequested, ref short pActiveCashier);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "Json_FP3_Echo", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 Json_FP3_Echo(UInt32 hInt, byte[] szEcho_Out, int EchoLen_Out, int TimeoutInMiliseconds);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "Json_FP3_GetPaymentApplicationInfo", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 Json_FP3_GetPaymentApplicationInfo(UInt32 hInt, ref byte pNumberOfTotalRecords, ref byte pNumberOfTotalRecordsReceived, byte[] szExchange, byte[] szExchange_Out, int ExchangeLen_Out, byte NumberOfRecordsRequested);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "Json_FP3_SetOnlineInvoice", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 Json_FP3_SetOnlineInvoice(UInt32 hInt, UInt64 hTrx, byte[] szJsonInvoiceInfo, byte[] szTicket_Out, int TicketLen_Out, int TimeoutInMiliseconds);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "Json_FP3_SetTaxFreeInfo", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 Json_FP3_SetTaxFreeInfo(UInt32 hInt, UInt64 hTrx, byte[] szJsonTaxFreeInfo, byte[] szTicket_Out, int TicketLen_Out, int TimeoutInMiliseconds);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "Json_FP3_SetInvoice", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 Json_FP3_SetInvoice(UInt32 hInt, UInt64 hTrx, byte[] szJsonInvoiceInfo, byte[] szJsonInvoiceInfo_Out, int JsonInvoiceInfoLen_Out, byte[] szTicket_Out, int TicketLen_Out, int TimeoutInMiliseconds);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "Json_FP3_SetTaxFree", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 Json_FP3_SetTaxFree(UInt32 hInt, UInt64 hTrx, byte[] szJsonTaxFreeInfo, byte[] szTicket_Out, int TicketLen_Out, int TimeoutInMiliseconds);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "Json_FP3_StartPairingInit", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 Json_FP3_StartPairingInit(UInt32 hInt, byte[] szPairing, byte[] szPairingResp, int PairingRespLen);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "Json_prepare_ItemSale", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern int Json_prepare_ItemSale(byte[] Buffer, int MaxSize, byte[] szJsonItem, byte[] szJsonItem_Out, int JsonItemLen_Out);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "Json_prepare_Payment", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern int Json_prepare_Payment(byte[] Buffer, int MaxSize, byte[] szJsonPaymentRequest, byte[] szJsonPaymentRequest_Out, int JsonPaymentRequestLen_Out);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "Json_FP3_FunctionReports", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 Json_FP3_FunctionReports(UInt32 hInt, int FunctionFlags, byte[] szJsonFunctionParameters, byte[] szJsonFunctionParameters_Out, int JsonFunctionParametersLen_Out, int TimeoutInMiliseconds);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "Json_FP3_FunctionReadZReport", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 Json_FP3_FunctionReadZReport(UInt32 hInt, byte[] szJsonFunctionParameters, byte[] szJsonZReport_Out, int JsonZReportLen_Out, int TimeoutInMiliseconds);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "Json_FP3_FunctionReadDM_Report", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 Json_FP3_FunctionReadDM_Report(UInt32 hInt, byte[] szJsonFunctionParameters, byte[] szJsonDM_Report_Out, int JsonDM_ReportLen_Out, int TimeoutInMiliseconds);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "Json_FP3_FunctionPaymentCheck", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 Json_FP3_FunctionPaymentCheck(UInt32 hInt, byte[] szJsonCheckResponse_In, byte[] szJsonCheckResponse_Out, int szJsonCheckResponseLen_Out, int TimeoutInMiliseconds);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "Json_prepare_SetInvoice", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern int Json_prepare_SetInvoice(byte[] Buffer, int MaxSize, byte[] szJsonInvoiceInfo, byte[] szJsonInvoiceInfo_Out, int JsonInvoiceInfoLen_Out);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "Json_prepare_SetOnlineInvoice", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern int Json_prepare_SetOnlineInvoice(byte[] Buffer, int MaxSize, byte[] szJsonInvoiceInfo);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "Json_prepare_SetTaxFreeInfo", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern int Json_prepare_SetTaxFreeInfo(byte[] Buffer, int MaxSize, byte[] szJsonTaxFreeInfo, byte[] szJsonTaxFreeInfo_Out, int JsonTaxFreeInfoLen_Out);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "Json_prepare_SetTaxFreeInfo", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern int Json_prepare_SetTaxFreeInfo(byte[] Buffer, int MaxSize, byte[] szJsonTaxFreeInfo);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "Json_prepare_PrintUserMessage", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern int Json_prepare_PrintUserMessage(byte[] Buffer, int MaxSize, byte[] szJsonUserMessage, byte[] szJsonUserMessage_Out, int JsonUserMessageLen_Out, ushort NumberOfMessage);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "Json_prepare_PrintUserMessage_Ex", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern int Json_prepare_PrintUserMessage_Ex(byte[] Buffer, int MaxSize, byte[] szJsonUserMessage, byte[] szJsonUserMessage_Out, int JsonUserMessageLen_Out, ushort NumberOfMessage);

        [DllImport("GMPSmartDLL.dll", EntryPoint = "Json_parse_FiscalPrinter", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 Json_parse_FiscalPrinter(byte[] szJsonReturnCodes_Out, int JsonReturnCodestLen_Out, ref UInt16 pNumberOfreturnCodes, uint RecvMsgId, byte[] RecvFullBuffer, UInt16 RecvFullLen, byte[] szJsonTicket_Out, int JsonTicketLen_Out, int MaxNumberOfReturnCode, int MaxReturnCodeDataLen);

        [DllImport("GMPSmartDLL.dll", EntryPoint = "Json_parse_GetEcr", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 Json_parse_GetEcr(byte[] szJsonReturnCodes_Out, int JsonReturnCodesLen_Out, ref int pNumberOfReturnCodes, UInt32 RecvMsgId, byte[] RecvFullBuffer, UInt16 RecvFullLen, int MaxNumberOfReturnCode, int MaxReturnCodeDataLen);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "Json_prepare_ReversePayment", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern int Json_prepare_ReversePayment(byte[] Buffer, int MaxSize, byte[] szJsonPaymentRequest, byte[] szJsonPaymentRequest_Out, int JsonPaymentRequestLen_Out, ushort NumberOfPaymentRequests);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "Json_prepare_Date", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern int Json_prepare_Date(byte[] Buffer, int MaxSize, UInt32 Tag_Id, byte[] Title, byte[] Text, byte[] Mask, byte[] szJsonValue, byte[] szJsonValue_Out, int JsonValueLen_Out, int timeout);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "Json_prepare_Condition", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern int Json_prepare_Condition(byte[] Buffer, int MaxSize, byte[] szJsonCondition, byte[] szJsonCondition_Out, int JsonConditionLen_Out);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "Json_FP3_ReversePayment", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 Json_FP3_ReversePayment(UInt32 hInt, UInt64 hTrx, byte[] szJsonPaymentRequest, byte[] szJsonPaymentRequest_Out, int JsonPaymentRequestLen_Out, short NumberOfPaymentRequests, byte[] szJsonTicket_Out, int JsonTicketLen_Out, int TimeoutInMiliseconds);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "Json_FP3_JumpToECR", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 Json_FP3_JumpToECR(UInt32 hInt, UInt64 hTrx, UInt64 JumpFlags, byte[] szJsonTicket_Out, int JsonTicketLen_Out, int TimeoutInMiliseconds);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "Json_FP3_MultipleCommand", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 Json_FP3_MultipleCommand(UInt32 hInt, ref UInt64 hTrx, byte[] stJsonReturnCodes_Out, int JsonReturnCodesLen_Out, ref ushort pIndexOfReturnCodes, byte[] SendBuffer, UInt16 SendBufferLen, byte[] szJsonTicket_Out, int JsonTicketLen_Out, int TimeoutInMiliseconds);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "Json_FP3_SetTaxFree", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 Json_FP3_SetTaxFree(UInt32 hInt, UInt64 hTrx, int Flag, byte[] szName, byte[] szSurname, byte[] szIdentificationNo, byte[] szCity, byte[] szCountry, byte[] szJsonTicket_Out, int JsonTicketLen_Out, int TimeoutInMiliseconds);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "Json_FP3_SetParkingTicket", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 Json_FP3_SetParkingTicket(UInt32 hInt, UInt64 hTrx, byte[] szCarIdentification, byte[] szJsonTicket_Out, int JsonTicketLen_Out, int TimeoutInMiliseconds);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "Json_FP3_SetTaxFreeRefundAmount", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 Json_FP3_SetTaxFreeRefundAmount(UInt32 hInt, UInt64 hTrx, int RefundAmount, ushort RefundAmountCurrency, byte[] szJsonTicket_Out, int JsonTicketLen_Out, int TimeoutInMiliseconds);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "Json_FP3_LoyaltyCustomerQuery", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 Json_FP3_LoyaltyCustomerQuery(UInt32 hInt, UInt64 hTrx, byte[] szJsonLoyaltyServiceInfo, byte[] szJsonLoyaltyServiceInfo_Out, int JsonLoyaltyServiceInfoLen_Out, byte[] szJsonTicket_Out, int JsonTicketLen_Out, int TimeoutInMiliseconds);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "Json_FP3_FunctionChangeTicketHeader", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 Json_FP3_FunctionChangeTicketHeader(UInt32 hInt, byte[] szSupervisorPassword, ref ushort pNumberOfSpaceTotal, ref ushort pNumberOfSpaceUsed, byte[] szJsonTicketHeader, byte[] szJsonTicketHeader_Out, int JsonTicketHeaderLen_Out, int TimeoutInMiliseconds);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "Json_FP3_GetTicketHeader", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 Json_FP3_GetTicketHeader(UInt32 hInt, ushort IndexOfHeader, byte[] szJsonTicketHeader, byte[] szJsonTicketHeader_Out, int JsonTicketHeaderLen_Out, ref ushort pNumberOfSpaceTotal, int TimeoutInMiliseconds);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "Json_FP3_GetOnlineInvoiceInfo", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 Json_FP3_GetOnlineInvoiceInfo(UInt32 hInt, byte[] szOnlineInvoiceId, int OnlineInvoiceIdLen, byte[] szJsonOnlineInvoiceInfo_Out, int JsonOnlineInvoiceInfoLen_Out, int TimeoutInMiliseconds);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "Json_FP3_Database_QueryColomnCaptions", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 Json_FP3_Database_QueryColomnCaptions(UInt32 hInt, byte[] szJsonDatabaseResult_Out, int JsonDatabaseResultLen_Out);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "Json_FP3_Database_QueryReadLine", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 Json_FP3_Database_QueryReadLine(UInt32 hInt, ushort NumberOfLinesRequested, ushort NumberOfColomnsRequested, byte[] szJsonDatabaseResult_Out, int JsonDatabaseResultLen_Out);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "Json_FP3_Database_FreeStructure", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 Json_FP3_Database_FreeStructure(UInt32 hInt, byte[] szJsonDatabaseResult, byte[] szJsonDatabaseResult_Out, int JsonDatabaseResultLen_Out);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "Json_FP3_Database_Execute", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 Json_FP3_Database_Execute(UInt32 hInt, byte[] szSqlWord, byte[] szJsonDatabaseResult_Out, int JsonDatabaseResultLen_Out);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "Json_FP3_GetPLU", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 Json_FP3_GetPLU(UInt32 hInt, byte[] szBarcode, byte[] szJsonPluRecord, byte[] szJsonPluRecord_Out, int JsonPluRecordLen_Out, byte[] szJsonPluGroupRecord, byte[] szJsonPluGroupRecord_Out, int szJsonPluGroupRecordLen_Out, int MaxNumberOfGroupRecords, int TimeoutInMiliseconds);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "Json_FP3_GetVasApplicationInfo", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 Json_FP3_GetVasApplicationInfo(UInt32 hInt, ref byte pNumberOfTotalRecords, ref byte pNumberOfTotalRecordsReceived, byte[] szJsonPaymentApplicationInfo_Out, int JsonPaymentApplicationInfoLen_Out, UInt16 vasType);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "Json_FP3_GetVasLoyaltyServiceInfo", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 Json_FP3_GetVasLoyaltyServiceInfo(UInt32 hInt, ref byte pNumberOfTotalRecords, ref byte pNumberOfTotalRecordsReceived, byte[] szJsonVasApplicationInfo_Out, int JsonVasApplicationInfoLen_Out, UInt16 VasAppId);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "Json_FP3_FunctionEkuSeek", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 Json_FP3_FunctionEkuSeek(UInt32 hInt, byte[] szJsonEKUAppInfo, byte[] szJsonEKUAppInfo_Out, int JsonEKUAppInfoLen_Out, int TimeoutInMiliseconds);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "Json_FP3_FileSystem_DirListFiles", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 Json_FP3_FileSystem_DirListFiles(UInt32 hInt, byte[] szDirName, byte[] szJsonStFile, byte[] szJsonStFile_Out, int JsonStFileLen_Out, short MaxNumberOfFiles, ref short pNumberOfFiles);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "Json_FP3_FunctionEkuReadHeader", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 Json_FP3_FunctionEkuReadHeader(UInt32 hInt, short Index, byte[] szJsonEkuHeader, byte[] szJsonEkuHeader_Out, int JsonEkuHeaderLen_Out, int TimeoutInMiliseconds);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "Json_FP3_FunctionEkuReadData", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 Json_FP3_FunctionEkuReadData(UInt32 hInt, ref UInt16 pEkuDataBufferReceivedLen, byte[] szJsonEKUAppInfo, byte[] szJsonEKUAppInfo_Out, int JsonEKUAppInfoLen_Out, int TimeoutInMiliseconds);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "Json_FP3_FunctionEkuReadInfo", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 Json_FP3_FunctionEkuReadInfo(UInt32 hInt, UInt16 EkuAccessFunction, byte[] szJsonEkuModuleInfo, byte[] szJsonEkuModuleInfo_Out, int JsonEkuModuleInfoLen_Out, int TimeoutInMiliseconds);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "Json_FP3_FunctionModuleReadInfo", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 Json_FP3_FunctionModuleReadInfo(UInt32 hInt, int AccessFunction, byte[] szJsonModuleInfo, byte[] szJsonModuleInfo_Out, int JsonModuleInfoLen_Out, int TimeoutInMiliseconds);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "Json_FP3_FunctionBankingRefund", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 Json_FP3_FunctionBankingRefund(UInt32 hInt, byte[] szJsonPaymentRequest, byte[] szJsonPaymentRequest_Out, int JsonPaymentRequestLen_Out, int TimeoutInMiliseconds);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "Json_FP3_FunctionBankingRefundExt", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 Json_FP3_FunctionBankingRefundExt(UInt32 hInt, byte[] szJsonPaymentRequest, byte[] szJsonPaymentRequest_Out, int JsonPaymentRequestLen_Out, byte[] szJsonPaymentResponse_Out, int szJsonPaymentResponseLen, int TimeoutInMiliseconds);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "Json_FP3_FunctionBankingBatch", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 Json_FP3_FunctionBankingBatch(UInt32 hInt, UInt16 BkmId, ref UInt16 pNumberOfBankResponse, byte[] szJsonMultipleBankResponse_Out, int JsonMultipleBankResponseLen_Out, int TimeoutInMiliseconds);
        
        [DllImport("GmpSmartDLL.dll", EntryPoint = "Json_SetIniParameters", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 Json_SetIniParameters(string szJsonIniParameter);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "Json_GetIniParameters", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 Json_GetIniParameters(byte[] szJsonIniParameter_Out, int szJsonIniParameterLen_Out);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "Json_FP3_FunctionGetHandleList", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 Json_FP3_FunctionGetHandleList(UInt32 hInt, byte[] szJsonHandleList_Out, int JsonHandleListLen_Out, byte StatusFilter, UInt16 StartIndexOfHandle, UInt16 HandleListSize, ref UInt16 TotalNumberOfHandlesInEcr, ref UInt16 ReceivedNumberOfHandleInList, int TimeoutInMiliseconds);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "Json_FP3_FunctionTransactionInquiry", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 Json_FP3_FunctionTransactionInquiry(UInt32 hInt, byte[] szJsonTransInquiry, byte[] szJsonTransInquiry_Out, int JsonTransInquiryLen_Out, int TimeoutInMiliseconds);

        public static UInt32 FP3_CreateInterface(ref UInt32 phInt, string szID, byte IsDefault, ST_INTERFACE_XML_DATA pstXmlData)
        {
            string szJsonXmlData = JsonConvert.SerializeObject(pstXmlData);
            byte[] szJsonXmlData_In = GMP_Tools.GetBytesFromString(szJsonXmlData);
            byte[] HandleID = GMP_Tools.GetBytesFromString(szID);

            return Json_FP3_CreateInterface(ref phInt, HandleID, IsDefault, szJsonXmlData_In);
        }

        public static UInt32 FP3_GetInterfaceXmlDataByID(string szID, ref ST_INTERFACE_XML_DATA pStInterfaceXmlData)
        {
            byte[] szJsonInterfaceXmlData_Out = new byte[Defines.STANDART_BUFFER];
            byte[] HandleID = GMP_Tools.GetBytesFromString(szID);

            UInt32 retcode = Json_FP3_GetInterfaceXmlDataByID(HandleID, szJsonInterfaceXmlData_Out, szJsonInterfaceXmlData_Out.Length);
            if (retcode == 0)
            {
                string retJsonString = GMP_Tools.GetStringFromBytes(szJsonInterfaceXmlData_Out);
                pStInterfaceXmlData = JsonConvert.DeserializeObject<ST_INTERFACE_XML_DATA>(retJsonString);
            }
            return retcode;
        }

        public static UInt32 FP3_GetInterfaceXmlDataByHandle(UInt32 hInt, ref ST_INTERFACE_XML_DATA stXmlData)
        {
            byte[] szJsonOut = new byte[Defines.STANDART_BUFFER];

            UInt32 retcode = Json_FP3_GetInterfaceXmlDataByHandle(hInt, szJsonOut, szJsonOut.Length);

            if (retcode == 0)
            {
                string retJsonString = GMP_Tools.SetEncoding(szJsonOut);
                stXmlData = JsonConvert.DeserializeObject<ST_INTERFACE_XML_DATA>(retJsonString);
            }
            return retcode;
        }

        public static UInt32 FP3_UpdateInterfaceXmlDataByID(string szID, ref ST_INTERFACE_XML_DATA pstInterfaceXmlData)
        {
            string szJsonXmlData = JsonConvert.SerializeObject(pstInterfaceXmlData);
            byte[] szJsonXmlData_In = GMP_Tools.GetBytesFromString(szJsonXmlData);
            byte[] HandleID = GMP_Tools.GetBytesFromString(szID);

            return Json_FP3_UpdateInterfaceXmlDataByID(HandleID, szJsonXmlData_In);
        }

        public static UInt32 FP3_UpdateInterfaceXmlDataByHandle(UInt32 hInt, ref ST_INTERFACE_XML_DATA pstInterfaceXmlData)
        {
            string szJsonXmlData = JsonConvert.SerializeObject(pstInterfaceXmlData);
            byte[] szJsonXmlData_In = GMP_Tools.GetBytesFromString(szJsonXmlData);

            return Json_FP3_UpdateInterfaceXmlDataByHandle(hInt, szJsonXmlData_In);
        }

        public static UInt32 FP3_GetGlobalXmlData(ref ST_GLOBAL_XML_DATA StGlobalXmlData)
        {
            string szJsonGlobalXmlData = JsonConvert.SerializeObject(StGlobalXmlData);
            byte[] szJsonGlobalXmlData_In = GMP_Tools.GetBytesFromString(szJsonGlobalXmlData);
            byte[] szJsonGlobalXmlData_Out = new byte[Defines.STANDART_BUFFER];

            UInt32 retcode = Json_FP3_GetGlobalXmlData(szJsonGlobalXmlData_Out, szJsonGlobalXmlData_Out.Length);
            if (retcode == 0)
            {
                string retJsonString = GMP_Tools.GetStringFromBytes(szJsonGlobalXmlData_Out);
                StGlobalXmlData = JsonConvert.DeserializeObject<ST_GLOBAL_XML_DATA>(retJsonString);
            }
            return retcode;
        }

        public static UInt32 FP3_UpdateGlobalXmlData(ref ST_GLOBAL_XML_DATA StGlobalXmlData)
        {
            string szJsonGlobalXmlData = JsonConvert.SerializeObject(StGlobalXmlData);
            byte[] szJsonGlobalXmlData_In = GMP_Tools.GetBytesFromString(szJsonGlobalXmlData);

            UInt32 retcode = Json_FP3_UpdateGlobalXmlData(szJsonGlobalXmlData_In);

            return retcode;
        }

        private static void MergeItemStruct(ST_TICKET StTicketDest, ST_TICKET StTicketSrc)
        {
            StTicketDest.szTicketDate = StTicketSrc.szTicketDate;
            StTicketDest.szTicketTime = StTicketSrc.szTicketTime;
            StTicketDest.SourceVasAppID = StTicketSrc.SourceVasAppID;
            StTicketDest.PaymentVasAppID = StTicketSrc.PaymentVasAppID;
            StTicketDest.BankVasAppID = StTicketSrc.BankVasAppID;
            StTicketDest.CashBackAmount = StTicketSrc.CashBackAmount;
            StTicketDest.EJNo = StTicketSrc.EJNo;
            StTicketDest.FNo = StTicketSrc.FNo;
            StTicketDest.invoiceAmount = StTicketSrc.invoiceAmount;
            StTicketDest.invoiceAmountCurrency = StTicketSrc.invoiceAmountCurrency;
            StTicketDest.invoiceDate = StTicketSrc.invoiceDate;
            StTicketDest.invoiceNo = StTicketSrc.invoiceNo;
            StTicketDest.invoiceType = StTicketSrc.invoiceType;
            StTicketDest.KasaAvansAmount = StTicketSrc.KasaAvansAmount;
            StTicketDest.KasaPaymentAmount = StTicketSrc.KasaPaymentAmount;
            StTicketDest.KatkiPayiAmount = StTicketSrc.KatkiPayiAmount;
            StTicketDest.numberOfItemsInThis = StTicketSrc.numberOfItemsInThis;
            StTicketDest.numberOfPaymentsInThis = StTicketSrc.numberOfPaymentsInThis;
            StTicketDest.numberOfPrinterLinesInThis = StTicketSrc.numberOfPrinterLinesInThis;
            StTicketDest.OptionFlags = StTicketSrc.OptionFlags;
            StTicketDest.TaxFreeCalculated = StTicketSrc.TaxFreeCalculated;
            StTicketDest.TaxFreeRefund = StTicketSrc.TaxFreeRefund;
            StTicketDest.TckNo = StTicketSrc.TckNo;
            StTicketDest.ticketType = StTicketSrc.ticketType;
            StTicketDest.totalNumberOfItems = StTicketSrc.totalNumberOfItems;
            StTicketDest.totalNumberOfPayments = StTicketSrc.totalNumberOfPayments;
            StTicketDest.totalNumberOfPrinterLines = StTicketSrc.totalNumberOfPrinterLines;
            StTicketDest.numberOfLoyaltyInThis = StTicketSrc.numberOfLoyaltyInThis;
            StTicketDest.TotalReceiptAmount = StTicketSrc.TotalReceiptAmount;
            StTicketDest.TotalReceiptDiscount = StTicketSrc.TotalReceiptDiscount;
            StTicketDest.TotalReceiptIncrement = StTicketSrc.TotalReceiptIncrement;
            StTicketDest.TotalReceiptItemCancel = StTicketSrc.TotalReceiptItemCancel;
            StTicketDest.TotalReceiptPayment = StTicketSrc.TotalReceiptPayment;
            StTicketDest.TotalReceiptReversedPayment = StTicketSrc.TotalReceiptReversedPayment;
            StTicketDest.TotalReceiptTax = StTicketSrc.TotalReceiptTax;
            StTicketDest.TransactionFlags = StTicketSrc.TransactionFlags;
            StTicketDest.uniqueId = StTicketSrc.uniqueId;
            StTicketDest.ZNo = StTicketSrc.ZNo;
            StTicketDest.UserData = StTicketSrc.UserData;
            StTicketDest.LastPaymentErrorCode = StTicketSrc.LastPaymentErrorCode;
            StTicketDest.LastPaymentErrorMsg = StTicketSrc.LastPaymentErrorMsg;
            StTicketDest.BankPaymentUniqueId = StTicketSrc.BankPaymentUniqueId;
            StTicketDest.CurrencyProfileIndex = StTicketSrc.CurrencyProfileIndex;

            StTicketDest.stTaxDetails = new ST_VATDetail[StTicketSrc.stTaxDetails.Length];
            for (int i = 0; i < StTicketDest.stTaxDetails.Length; i++)
            {
                StTicketDest.stTaxDetails[i] = new ST_VATDetail();
            }

            StTicketDest.stPrinterCopy = new ST_printerDataForOneLine[StTicketSrc.totalNumberOfPrinterLines];
            for (int i = 0; i < StTicketDest.stPrinterCopy.Length; i++)
            {
                StTicketDest.stPrinterCopy[i] = new ST_printerDataForOneLine();
            }

            //StTicketDest.SaleInfo = new ST_SALEINFO[StTicketSrc.totalNumberOfItems];
            //for (int i = 0; i < StTicketDest.SaleInfo.Length; i++)
            //{
            //    StTicketDest.SaleInfo[i] = new ST_SALEINFO();
            //}

            //StTicketDest.stPayment = new ST_PAYMENT[StTicketSrc.totalNumberOfPayments];
            //for (int i = 0; i < StTicketDest.stPrinterCopy.Length; i++)
            //{
            //    StTicketDest.stPayment[i] = new ST_PAYMENT();
            //}

            for (int i = 0; i < StTicketSrc.stTaxDetails.Length; ++i)
            {
                if (StTicketSrc.stTaxDetails != null && StTicketSrc.stTaxDetails[i] != null)
                    StTicketDest.stTaxDetails[i] = StTicketSrc.stTaxDetails[i];
            }

            //for (int i = 0; i < StTicketSrc.totalNumberOfItems; ++i)
            //{
            //    if (StTicketSrc.SaleInfo != null && StTicketSrc.SaleInfo[i] != null)
            //        StTicketDest.SaleInfo[i] = StTicketSrc.SaleInfo[i];
            //}

            for (int i = 0; i < StTicketSrc.totalNumberOfPayments; ++i)
            {
                if (StTicketSrc.stPayment != null && StTicketSrc.stPayment[i] != null)
                    StTicketDest.stPayment[i] = StTicketSrc.stPayment[i];
            }

            for (int i = 0; i < StTicketSrc.numberOfLoyaltyInThis; ++i)
            {
                if (StTicketSrc.stLoyaltyService != null && StTicketSrc.stLoyaltyService[i] != null)
                    StTicketDest.stLoyaltyService[i] = StTicketSrc.stLoyaltyService[i];
            }

            for (int i = 0; i < StTicketSrc.totalNumberOfPrinterLines; ++i)
            {
                if (StTicketSrc.stPrinterCopy != null && StTicketSrc.stPrinterCopy[i] != null)
                    StTicketDest.stPrinterCopy[i] = StTicketSrc.stPrinterCopy[i];
            }
        }

        private static void MergeTaxRateStruct(ST_TAX_RATE[] StTaxRateDest, ST_TAX_RATE[] StTaxRateSrc, int index, int size)
        {
            int j = 0;
            for (int i = index; i < index + size; i++)
            {
                StTaxRateDest[i].taxRate = StTaxRateSrc[j++].taxRate;
            }
        }

        private static void MergeDepartmentStruct(ST_DEPARTMENT[] StDepartmentDest, ST_DEPARTMENT[] StDepartmentSrc, int index, int size)
        {
            int j = 0;
            for (int i = index; i < index + size; i++)
            {
                StDepartmentDest[i].iCurrencyType = StDepartmentSrc[j].iCurrencyType;
                StDepartmentDest[i].iUnitType = StDepartmentSrc[j].iUnitType;
                StDepartmentDest[i].szDeptName = StDepartmentSrc[j].szDeptName;
                StDepartmentDest[i].u64Limit = StDepartmentSrc[j].u64Limit;
                StDepartmentDest[i].u64Price = StDepartmentSrc[j].u64Price;
                StDepartmentDest[i].u8TaxIndex = StDepartmentSrc[j].u8TaxIndex;
                j++;
            }
        }

        public static UInt32 FP3_GetTaxRates(UInt32 hInt, ref int pNumberOfTotalRecords, ref int pNumberOfTotalRecordsReceived, ref ST_TAX_RATE[] pStTaxRate, int NumberOfRecordsRequested)
        {
            string szJsonTaxRates = JsonConvert.SerializeObject(pStTaxRate);
            byte[] szJsonTaxRates_In = GMP_Tools.GetBytesFromString(szJsonTaxRates);
            byte[] szJsonTaxRates_Out = new byte[Defines.STANDART_BUFFER];

            UInt32 retcode = Json_FP3_GetTaxRates(hInt, ref pNumberOfTotalRecords, ref pNumberOfTotalRecordsReceived, szJsonTaxRates_In, szJsonTaxRates_Out, szJsonTaxRates_Out.Length, NumberOfRecordsRequested);
            if (retcode == 0)
            {
                string retJsonString = GMP_Tools.GetStringFromBytes(szJsonTaxRates_Out);
                pStTaxRate = JsonConvert.DeserializeObject<ST_TAX_RATE[]>(retJsonString);
            }
            return retcode;
        }

        public static UInt32 FP3_GetDepartments(UInt32 hInt, ref int pNumberOfTotalDepartments, ref int pNumberOfTotalDepartmentsReceived, ref ST_DEPARTMENT[] pStDepartments, int NumberOfDepartmentRequested)
        {
            string szJsonDepartments = JsonConvert.SerializeObject(pStDepartments);
            byte[] szJsonDepartments_In = GMP_Tools.GetBytesFromString(szJsonDepartments);
            byte[] szJsonDepartments_Out = new byte[Defines.STANDART_BUFFER];

            UInt32 retcode = Json_FP3_GetDepartments(hInt, ref pNumberOfTotalDepartments, ref pNumberOfTotalDepartmentsReceived, szJsonDepartments_In, szJsonDepartments_Out, szJsonDepartments_Out.Length, NumberOfDepartmentRequested);
            if (retcode == 0)
            {
                string retJsonString = GMP_Tools.GetStringFromBytes(szJsonDepartments_Out);
                pStDepartments = JsonConvert.DeserializeObject<ST_DEPARTMENT[]>(retJsonString);
            }
            return retcode;
        }

        public static UInt32 FP3_GetTaxRates_Ex(UInt32 hInt, byte indexOfTaxRates, ref int pNumberOfTotalRecords, ref int pNumberOfTotalRecordsReceived, ref ST_TAX_RATE[] pStTaxRateOrg, int NumberOfRecordsRequested)
        {
            ST_TAX_RATE[] pStTaxRate = new ST_TAX_RATE[NumberOfRecordsRequested];
            string szJsonTaxRates = JsonConvert.SerializeObject(pStTaxRate);
            byte[] szJsonTaxRates_In = GMP_Tools.GetBytesFromString(szJsonTaxRates);
            byte[] szJsonTaxRates_Out = new byte[Defines.STANDART_BUFFER];

            UInt32 retcode = Json_FP3_GetTaxRates_Ex(hInt, indexOfTaxRates, ref pNumberOfTotalRecords, ref pNumberOfTotalRecordsReceived, szJsonTaxRates_In, szJsonTaxRates_Out, szJsonTaxRates_Out.Length, NumberOfRecordsRequested);
            if (retcode == 0)
            {
                string retJsonString = GMP_Tools.GetStringFromBytes(szJsonTaxRates_Out);
                pStTaxRate = JsonConvert.DeserializeObject<ST_TAX_RATE[]>(retJsonString);

                MergeTaxRateStruct(pStTaxRateOrg, pStTaxRate, indexOfTaxRates, pNumberOfTotalRecordsReceived);
            }
            return retcode;
        }

        public static UInt32 FP3_GetDepartments_Ex(UInt32 hInt, byte indexOfDepartments, ref int pNumberOfTotalDepartments, ref int pNumberOfTotalDepartmentsReceived, ref ST_DEPARTMENT[] pStDepartmentsOrg, int NumberOfDepartmentRequested)
        {
            ST_DEPARTMENT[] pStDepartments = new ST_DEPARTMENT[NumberOfDepartmentRequested];
            string szJsonDepartments = JsonConvert.SerializeObject(pStDepartments);
            byte[] szJsonDepartments_In = GMP_Tools.GetBytesFromString(szJsonDepartments);
            byte[] szJsonDepartments_Out = new byte[Defines.STANDART_BUFFER];

            UInt32 retcode = Json_FP3_GetDepartments_Ex(hInt, indexOfDepartments, ref pNumberOfTotalDepartments, ref pNumberOfTotalDepartmentsReceived, szJsonDepartments_In, szJsonDepartments_Out, szJsonDepartments_Out.Length, NumberOfDepartmentRequested);
            if (retcode == 0)
            {
                string retJsonString = GMP_Tools.GetStringFromBytes(szJsonDepartments_Out);
                pStDepartments = JsonConvert.DeserializeObject<ST_DEPARTMENT[]>(retJsonString);

                MergeDepartmentStruct(pStDepartmentsOrg, pStDepartments, indexOfDepartments, pNumberOfTotalDepartmentsReceived);
            }
            return retcode;
        }

        public static UInt32 FP3_GetCurrencyProfile(UInt32 hInt, ref ST_EXCHANGE_PROFILE[] pStExchangeProfile)
        {
            string szJsonExchangeProfileTable = JsonConvert.SerializeObject(pStExchangeProfile);
            byte[] szJsonExchangeProfileTable_In = GMP_Tools.GetBytesFromString(szJsonExchangeProfileTable);
            byte[] szJsonExchangeProfileTable_Out = new byte[Defines.STANDART_BUFFER];

            UInt32 retcode = Json_FP3_GetCurrencyProfile(hInt, szJsonExchangeProfileTable_In, szJsonExchangeProfileTable_Out, szJsonExchangeProfileTable_Out.Length);

            if (retcode == 0)
            {
                string retJsonString = GMP_Tools.GetStringFromBytes(szJsonExchangeProfileTable_Out);
                pStExchangeProfile = (JsonConvert.DeserializeObject<List<ST_EXCHANGE_PROFILE>>(retJsonString)).ToArray();
            }
            return retcode;
        }

        public static UInt32 FP3_SetCurrencyProfile(UInt32 hInt, string supervisorPassword, byte profileIndex, byte ProfileProcessType, ST_EXCHANGE_PROFILE[] pStExchangeProfile)
        {
            string szJsonExchangeProfileTable = JsonConvert.SerializeObject(pStExchangeProfile);
            byte[] szJsonExchangeProfileTable_In = GMP_Tools.GetBytesFromString(szJsonExchangeProfileTable);
            byte[] szJsonExchangeProfileTable_Out = new byte[Defines.STANDART_BUFFER];

            UInt32 retcode = Json_FP3_SetCurrencyProfile(hInt, GMP_Tools.GetBytesFromString(supervisorPassword), profileIndex, ProfileProcessType, szJsonExchangeProfileTable_In);
            //if (retcode == 0)
            //{
            //    string retJsonString = GMP_Tools.GetStringFromBytes(szJsonExchangeProfileTable_Out);
            //    pStExchangeProfile = (JsonConvert.DeserializeObject<List<ST_EXCHANGE_PROFILE>>(retJsonString)).ToArray();
            //}
            return retcode;
        }

        public static UInt32 FP3_SetCurrencyProfileIndex(UInt32 hInt, UInt64 hTrx, byte index, ST_TICKET stTicket, int TimeoutInMiliseconds)
        {
            string szJsonStTicket = JsonConvert.SerializeObject(stTicket);
            byte[] szJsonStTicket_In = GMP_Tools.GetBytesFromString(szJsonStTicket);

            UInt32 retcode = Json_FP3_SetCurrencyProfileIndex(hInt, hTrx, index, szJsonStTicket_In, TimeoutInMiliseconds);
            return retcode;
        }

        public static UInt32 FP3_SetDepartments(UInt32 hInt, ref ST_DEPARTMENT[] pStDepartments, byte NumberOfDepartmentRequested, string szSupervisorPassword)
        {
            string szJsonDepartments = JsonConvert.SerializeObject(pStDepartments);
            byte[] szJsonDepartments_In = GMP_Tools.GetBytesFromString(szJsonDepartments);
            byte[] supervisorPass = GMP_Tools.GetBytesFromString(szSupervisorPassword);

            byte[] szJsonDepartments_Out = new byte[Defines.STANDART_BUFFER];

            UInt32 retcode = Json_FP3_SetDepartments(hInt, szJsonDepartments_In, szJsonDepartments_Out, szJsonDepartments_Out.Length, NumberOfDepartmentRequested, supervisorPass);
            if (retcode == 0)
            {
                string retJsonString = GMP_Tools.GetStringFromBytes(szJsonDepartments_Out);
                pStDepartments = JsonConvert.DeserializeObject<ST_DEPARTMENT[]>(retJsonString);
            }
            return retcode;
        }

        public static UInt32 Json_FP3_CustomerAvans(UInt32 hInt, UInt64 hTrx, int Amount, ref ST_TICKET pstTicket, string szCustomerName, string szTckn, string szVkn, int TimeoutInMiliseconds)
        {
            byte[] szJsonTicket_Out = new byte[Defines.GMP_TICKET_BUFFER];
            byte[] CustomerName = GMP_Tools.GetBytesFromString(szCustomerName);
            byte[] Tckn = GMP_Tools.GetBytesFromString(szTckn);
            byte[] Vkn = GMP_Tools.GetBytesFromString(szVkn);

            UInt32 retcode = Json_FP3_CustomerAvans(hInt, hTrx, Amount, szJsonTicket_Out, szJsonTicket_Out.Length, CustomerName, Tckn, Vkn, TimeoutInMiliseconds);
            if (retcode == 0)
            {
                string retJsonString = GMP_Tools.GetStringFromBytes(szJsonTicket_Out);
                ST_TICKET StTicketTemp = new ST_TICKET();
                StTicketTemp = JsonConvert.DeserializeObject<ST_TICKET>(retJsonString);
                MergeItemStruct(pstTicket, StTicketTemp);
            }
            return retcode;
        }

        public static UInt32 Json_FP3_CariHesap(UInt32 hInt, UInt64 hTrx, int Amount, ref ST_TICKET pstTicket, string szCustomerName, string szTckn, string szVkn, string szBelgeNo, string szBelgeDate, int TimeoutInMiliseconds)
        {
            byte[] szJsonTicket_Out = new byte[Defines.GMP_TICKET_BUFFER];
            byte[] CustomerName = GMP_Tools.GetBytesFromString(szCustomerName);
            byte[] Tckn = GMP_Tools.GetBytesFromString(szTckn);
            byte[] Vkn = GMP_Tools.GetBytesFromString(szVkn);
            byte[] BelgeNo = GMP_Tools.GetBytesFromString(szBelgeNo);
            //byte[] BelgeDate = GMP_Tools.GetBytesFromString(szBelgeDate);
            byte[] BelgeDate = GMP_Tools.GetBytesFromString(szBelgeDate);

            UInt32 retcode = Json_FP3_CariHesap(hInt, hTrx, Amount, szJsonTicket_Out, szJsonTicket_Out.Length, CustomerName, Tckn, Vkn, BelgeNo, BelgeDate, TimeoutInMiliseconds);
            if (retcode == 0)
            {
                string retJsonString = GMP_Tools.GetStringFromBytes(szJsonTicket_Out);
                ST_TICKET StTicketTemp = new ST_TICKET();
                StTicketTemp = JsonConvert.DeserializeObject<ST_TICKET>(retJsonString);
                MergeItemStruct(pstTicket, StTicketTemp);
            }
            return retcode;
        }

        public static UInt32 SetIniParameter(ST_INI_PARAM pStIniParameter)
        {
            string szJsonIniParameter = JsonConvert.SerializeObject(pStIniParameter);

            UInt32 retcode = Json_SetIniParameters(szJsonIniParameter);

            return retcode;
        }

        public static UInt32 GetIniParameters(ref ST_INI_PARAM pStIniParameter)
        {
            byte[] szJsonIniParameter_Out = new byte[Defines.GMP_TICKET_BUFFER];

            UInt32 retcode = Json_GetIniParameters(szJsonIniParameter_Out, szJsonIniParameter_Out.Length);

            if (retcode == 0)
            {
                string retJsonString = GMP_Tools.SetEncoding(szJsonIniParameter_Out);
                pStIniParameter = JsonConvert.DeserializeObject<ST_INI_PARAM>(retJsonString);
            }
            return retcode;
        }

        public static UInt32 FP3_FunctionGetHandleList(UInt32 hInt, ref ST_HANDLE_LIST[] stHandleList, byte StatusFilter, UInt16 StartIndexOfHandle, UInt16 HandleListSize, ref UInt16 TotalNumberOfHandlesInEcr, ref UInt16 ReceivedNumberOfHandleInList, int TimeoutInMiliseconds)
        {
            byte[] szJsonOut = new byte[Defines.GMP_TICKET_BUFFER];

            UInt32 retcode = Json_FP3_FunctionGetHandleList(hInt, szJsonOut, szJsonOut.Length, StatusFilter, StartIndexOfHandle, HandleListSize, ref TotalNumberOfHandlesInEcr, ref ReceivedNumberOfHandleInList, TimeoutInMiliseconds);

            if (retcode == 0)
            {
                string retJsonString = GMP_Tools.SetEncoding(szJsonOut);
                stHandleList = JsonConvert.DeserializeObject<ST_HANDLE_LIST[]>(retJsonString);
            }

            return retcode;
        }

        public static UInt32 FP3_KasaAvans(UInt32 hInt, UInt64 hTrx, int Amount, ref ST_TICKET pstTicket, int TimeoutInMiliseconds)
        {
            byte[] szJsonTicket_Out = new byte[Defines.GMP_TICKET_BUFFER];

            UInt32 retcode = Json_FP3_KasaAvans(hInt, hTrx, Amount, szJsonTicket_Out, szJsonTicket_Out.Length, TimeoutInMiliseconds);
            if (retcode == 0)
            {
                string retJsonString = GMP_Tools.GetStringFromBytes(szJsonTicket_Out);
                ST_TICKET StTicketTemp = new ST_TICKET();
                StTicketTemp = JsonConvert.DeserializeObject<ST_TICKET>(retJsonString);
                MergeItemStruct(pstTicket, StTicketTemp);
            }
            return retcode;
        }

        public static UInt32 FP3_KasaPayment(UInt32 hInt, UInt64 hTrx, int Amount, ref ST_TICKET pstTicket, int TimeoutInMiliseconds)
        {
            byte[] szJsonTicket_Out = new byte[Defines.STANDART_BUFFER];

            UInt32 retcode = Json_FP3_KasaPayment(hInt, hTrx, Amount, szJsonTicket_Out, szJsonTicket_Out.Length, TimeoutInMiliseconds);
            if (retcode == 0)
            {
                string retJsonString = GMP_Tools.GetStringFromBytes(szJsonTicket_Out);
                ST_TICKET StTicketTemp = new ST_TICKET();
                StTicketTemp = JsonConvert.DeserializeObject<ST_TICKET>(retJsonString);
                MergeItemStruct(pstTicket, StTicketTemp);
            }
            return retcode;
        }

        public static UInt32 FP3_ItemSale(UInt32 hInt, UInt64 hTrx, ref ST_ITEM StItem, ref ST_TICKET pstTicket, int TimeoutInMiliseconds)
        {
            byte[] szJsonTicket_Out = new byte[Defines.GMP_TICKET_BUFFER];
            string szJsonItem = JsonConvert.SerializeObject(StItem);
            byte[] szJsonItem_In = GMP_Tools.GetBytesFromString(szJsonItem);
            byte[] szJsonItem_Out = new byte[Defines.GMP_TICKET_BUFFER];


            UInt32 retcode = Json_FP3_ItemSale(hInt, hTrx, szJsonItem_In, szJsonItem_Out, szJsonItem_Out.Length, szJsonTicket_Out, szJsonTicket_Out.Length, TimeoutInMiliseconds);
            if (retcode == 0)
            {
                string retJsonString = GMP_Tools.GetStringFromBytes(szJsonItem_Out);
                StItem = JsonConvert.DeserializeObject<ST_ITEM>(retJsonString);
                retJsonString = GMP_Tools.GetStringFromBytes(szJsonTicket_Out);
                ST_TICKET StTicketTemp = new ST_TICKET();
                StTicketTemp = JsonConvert.DeserializeObject<ST_TICKET>(retJsonString);
                MergeItemStruct(pstTicket, StTicketTemp);
            }
            return retcode;
        }

        public static UInt32 FP3_Payment(UInt32 hInt, UInt64 hTrx, ref ST_PAYMENT_REQUEST pStPaymentRequest, ref ST_TICKET pstTicket, int TimeoutInMiliseconds)
        {
            byte[] szJsonTicket_Out = new byte[Defines.GMP_TICKET_BUFFER];
            string szJsonPaymentRequest = JsonConvert.SerializeObject(pStPaymentRequest);
            byte[] szJsonPaymentRequest_In = GMP_Tools.GetBytesFromString(szJsonPaymentRequest);
            byte[] szJsonPaymentRequest_Out = new byte[Defines.GMP_TICKET_BUFFER];

            UInt32 retcode = Json_FP3_Payment(hInt, hTrx, szJsonPaymentRequest_In, szJsonPaymentRequest_Out, szJsonPaymentRequest_Out.Length, szJsonTicket_Out, szJsonTicket_Out.Length, TimeoutInMiliseconds);

            if (retcode != 0)
            {
                return retcode;
            }

            string retJsonString = GMP_Tools.GetStringFromBytes(szJsonPaymentRequest_Out);
            pStPaymentRequest = JsonConvert.DeserializeObject<ST_PAYMENT_REQUEST>(retJsonString);
            retJsonString = GMP_Tools.GetStringFromBytes(szJsonTicket_Out);
            ST_TICKET StTicketTemp = new ST_TICKET();
            StTicketTemp = JsonConvert.DeserializeObject<ST_TICKET>(retJsonString);
            MergeItemStruct(pstTicket, StTicketTemp);

            return retcode;
        }

        public static UInt32 FP3_FunctionVasPaymentRefund(UInt32 hInt, ref ST_PAYMENT_REQUEST pStPaymentRequest, int TimeoutInMiliseconds)
        {
            string szJsonPaymentRequest = JsonConvert.SerializeObject(pStPaymentRequest);
            byte[] szJsonPaymentRequest_In = GMP_Tools.GetBytesFromString(szJsonPaymentRequest);
            byte[] szJsonPaymentRequest_Out = new byte[Defines.GMP_TICKET_BUFFER];

            UInt32 retcode = Json_FP3_FunctionVasPaymentRefund(hInt, szJsonPaymentRequest_In, szJsonPaymentRequest_Out, szJsonPaymentRequest_Out.Length, TimeoutInMiliseconds);

            if (retcode != 0)
            {
                return retcode;
            }

            string retJsonString = GMP_Tools.GetStringFromBytes(szJsonPaymentRequest_Out);
            pStPaymentRequest = JsonConvert.DeserializeObject<ST_PAYMENT_REQUEST>(retJsonString);

            return retcode;
        }

        public static UInt32 FP3_PrintUserMessage(UInt32 hInt, UInt64 hTrx, ref ST_USER_MESSAGE[] pStUser, UInt16 NumberOfMessage, ref ST_TICKET pstTicket, int TimeoutInMiliseconds)
        {
            byte[] szJsonTicket_Out = new byte[Defines.GMP_TICKET_BUFFER];
            string szJsonUser = JsonConvert.SerializeObject(pStUser);
            byte[] szJsonUser_In = GMP_Tools.GetBytesFromString(szJsonUser);
            byte[] szJsonUser_Out = new byte[Defines.GMP_TICKET_BUFFER];

            UInt32 retcode = Json_FP3_PrintUserMessage(hInt, hTrx, szJsonUser_In, szJsonUser_Out, szJsonUser_Out.Length, NumberOfMessage, szJsonTicket_Out, szJsonTicket_Out.Length, TimeoutInMiliseconds);
            if (retcode == 0)
            {
                string retJsonString = GMP_Tools.GetStringFromBytes(szJsonUser_Out);
                pStUser = JsonConvert.DeserializeObject<ST_USER_MESSAGE[]>(retJsonString);
            }
            return retcode;
        }

        public static UInt32 FP3_PrintUserMessage_Ex(UInt32 hInt, UInt64 hTrx, ref ST_USER_MESSAGE[] pStUser, UInt16 NumberOfMessage, ref ST_TICKET pstTicket, int TimeoutInMiliseconds)
        {
            byte[] szJsonTicket_Out = new byte[Defines.GMP_TICKET_BUFFER];
            string szJsonUser = JsonConvert.SerializeObject(pStUser);
            byte[] szJsonUser_In = GMP_Tools.GetBytesFromString(szJsonUser);
            byte[] szJsonUser_Out = new byte[Defines.GMP_TICKET_BUFFER];

            UInt32 retcode = Json_FP3_PrintUserMessage_Ex(hInt, hTrx, szJsonUser_In, szJsonUser_Out, szJsonUser_Out.Length, NumberOfMessage, szJsonTicket_Out, szJsonTicket_Out.Length, TimeoutInMiliseconds);
            if (retcode == 0)
            {
                string retJsonString = GMP_Tools.GetStringFromBytes(szJsonUser_Out);
                pStUser = JsonConvert.DeserializeObject<ST_USER_MESSAGE[]>(retJsonString);
            }
            return retcode;
        }

        public static UInt32 FP3_GetTicket(UInt32 hInt, UInt64 hTrx, ref ST_TICKET pstTicket, int TimeoutInMiliseconds)
        {
            byte[] szJsonTicket_Out = new byte[Defines.GMP_TICKET_BUFFER];

            UInt32 retcode = Json_FP3_GetTicket(hInt, hTrx, szJsonTicket_Out, szJsonTicket_Out.Length, TimeoutInMiliseconds);
            if (retcode == 0)
            {
                string retJsonString = GMP_Tools.GetStringFromBytes(szJsonTicket_Out);
                ST_TICKET StTicketTemp = new ST_TICKET();
                StTicketTemp = JsonConvert.DeserializeObject<ST_TICKET>(retJsonString);
                MergeItemStruct(pstTicket, StTicketTemp);
            }
            return retcode;
        }

        public static UInt32 FP3_Plus(UInt32 hInt, UInt64 hTrx, int Amount, string szText, ref ST_TICKET pstTicket, UInt16 IndexOfItem, int TimeoutInMiliseconds)
        {
            byte[] json_Out_stTicket = new byte[Defines.GMP_TICKET_BUFFER];
            byte[] Text = GMP_Tools.GetBytesFromString(szText);

            UInt32 retcode = Json_FP3_Plus(hInt, hTrx, Amount, Text, json_Out_stTicket, json_Out_stTicket.Length, IndexOfItem, TimeoutInMiliseconds);
            if (retcode == 0)
            {
                string retJsonString = GMP_Tools.GetStringFromBytes(json_Out_stTicket);
                ST_TICKET StTicketTemp = new ST_TICKET();
                StTicketTemp = JsonConvert.DeserializeObject<ST_TICKET>(retJsonString);
                MergeItemStruct(pstTicket, StTicketTemp);
            }
            return retcode;
        }

        public static UInt32 FP3_LoyaltyDiscount(UInt32 hInt, UInt64 hTrx, byte isRate, int Amount, byte Rate, string szLoyaltyCustomerId, string szText, UInt16 indexOfItem, ref int pchangedAmount, ref ST_TICKET pstTicket, int timeoutInMiliseconds)
        {
            byte[] json_Out_stTicket = new byte[Defines.GMP_TICKET_BUFFER];
            byte[] Text = GMP_Tools.GetBytesFromString(szText);
            byte[] LoyaltyCustomerId = GMP_Tools.GetBytesFromString(szLoyaltyCustomerId);

            UInt32 retcode = Json_FP3_LoyaltyDiscount(hInt, hTrx, isRate, Amount, Rate, LoyaltyCustomerId, Text, indexOfItem, ref pchangedAmount, json_Out_stTicket, json_Out_stTicket.Length, timeoutInMiliseconds);
            if (retcode == 0)
            {
                string retJsonString = GMP_Tools.GetStringFromBytes(json_Out_stTicket);
                ST_TICKET StTicketTemp = new ST_TICKET();
                StTicketTemp = JsonConvert.DeserializeObject<ST_TICKET>(retJsonString);
                MergeItemStruct(pstTicket, StTicketTemp);
            }
            return retcode;
        }

        public static UInt32 FP3_Minus(UInt32 hInt, UInt64 hTrx, int Amount, string szText, ref ST_TICKET pstTicket, UInt16 IndexOfItem, int TimeoutInMiliseconds)
        {
            byte[] json_Out_stTicket = new byte[Defines.GMP_TICKET_BUFFER];
            byte[] Text = GMP_Tools.GetBytesFromString(szText);

            UInt32 retcode = Json_FP3_Minus(hInt, hTrx, Amount, Text, json_Out_stTicket, json_Out_stTicket.Length, IndexOfItem, TimeoutInMiliseconds);
            if (retcode == 0)
            {
                string retJsonString = GMP_Tools.GetStringFromBytes(json_Out_stTicket);
                ST_TICKET StTicketTemp = new ST_TICKET();
                StTicketTemp = JsonConvert.DeserializeObject<ST_TICKET>(retJsonString);
                MergeItemStruct(pstTicket, StTicketTemp);
            }
            return retcode;
        }

        public static UInt32 FP3_Dec(UInt32 hInt, UInt64 hTrx, byte Rate, string szText, ref ST_TICKET pstTicket, UInt16 IndexOfItem, ref  int pChangedAmount, int TimeoutInMiliseconds)
        {
            byte[] szJsonTicket_Out = new byte[Defines.GMP_TICKET_BUFFER];
            byte[] Text = GMP_Tools.GetBytesFromString(szText);

            UInt32 retcode = Json_FP3_Dec(hInt, hTrx, Rate, Text, szJsonTicket_Out, szJsonTicket_Out.Length, IndexOfItem, ref pChangedAmount, TimeoutInMiliseconds);
            if (retcode == 0)
            {
                string retJsonString = GMP_Tools.GetStringFromBytes(szJsonTicket_Out);
                ST_TICKET StTicketTemp = new ST_TICKET();
                StTicketTemp = JsonConvert.DeserializeObject<ST_TICKET>(retJsonString);
                MergeItemStruct(pstTicket, StTicketTemp);
            }
            return retcode;
        }

        public static UInt32 FP3_Inc(UInt32 hInt, UInt64 hTrx, byte Rate, string szText, ref ST_TICKET pstTicket, UInt16 IndexOfItem, ref  int pChangedAmount, int TimeoutInMiliseconds)
        {
            byte[] szJsonTicket_Out = new byte[Defines.GMP_TICKET_BUFFER];
            byte[] Text = GMP_Tools.GetBytesFromString(szText);

            UInt32 retcode = Json_FP3_Inc(hInt, hTrx, Rate, Text, szJsonTicket_Out, szJsonTicket_Out.Length, IndexOfItem, ref pChangedAmount, TimeoutInMiliseconds);
            if (retcode == 0)
            {
                string retJsonString = GMP_Tools.SetEncoding(szJsonTicket_Out);
                ST_TICKET StTicketTemp = new ST_TICKET();
                StTicketTemp = JsonConvert.DeserializeObject<ST_TICKET>(retJsonString);
                MergeItemStruct(pstTicket, StTicketTemp);
            }
            return retcode;
        }

        public static UInt32 FP3_VoidAll(UInt32 hInt, UInt64 hTrx, ref ST_TICKET pstTicket, int TimeoutInMiliseconds)
        {
            byte[] szJsonTicket_Out = new byte[Defines.GMP_TICKET_BUFFER];

            UInt32 retcode = Json_FP3_VoidAll(hInt, hTrx, szJsonTicket_Out, szJsonTicket_Out.Length, TimeoutInMiliseconds);
            if (retcode == 0)
            {
                string retJsonString = GMP_Tools.GetStringFromBytes(szJsonTicket_Out);
                ST_TICKET StTicketTemp = new ST_TICKET();
                StTicketTemp = JsonConvert.DeserializeObject<ST_TICKET>(retJsonString);
                MergeItemStruct(pstTicket, StTicketTemp);
            }
            return retcode;
        }

        public static UInt32 FP3_Pretotal(UInt32 hInt, UInt64 hTrx, ref ST_TICKET pstTicket, int TimeoutInMiliseconds)
        {
            byte[] szJsonTicket_Out = new byte[Defines.GMP_TICKET_BUFFER];

            UInt32 retcode = Json_FP3_Pretotal(hInt, hTrx, szJsonTicket_Out, szJsonTicket_Out.Length, TimeoutInMiliseconds);
            if (retcode == 0)
            {
                string retJsonString = GMP_Tools.GetStringFromBytes(szJsonTicket_Out);
                ST_TICKET StTicketTemp = new ST_TICKET();
                StTicketTemp = JsonConvert.DeserializeObject<ST_TICKET>(retJsonString);
                MergeItemStruct(pstTicket, StTicketTemp);
            }
            return retcode;
        }

        public static UInt32 FP3_Matrahsiz(UInt32 hInt, UInt64 hTrx, string szTckNo, ushort SubtypeOfTaxException, int MatrahsizAmount, ref ST_TICKET pstTicket, int TimeoutInMiliseconds)
        {
            byte[] szJsonTicket_Out = new byte[Defines.GMP_TICKET_BUFFER];
            byte[] TckNo = GMP_Tools.GetBytesFromString(szTckNo);

            UInt32 retcode = Json_FP3_Matrahsiz(hInt, hTrx, TckNo, SubtypeOfTaxException, MatrahsizAmount, szJsonTicket_Out, szJsonTicket_Out.Length, TimeoutInMiliseconds);
            if (retcode == 0)
            {
                string retJsonString = GMP_Tools.GetStringFromBytes(szJsonTicket_Out);
                ST_TICKET StTicketTemp = new ST_TICKET();
                StTicketTemp = JsonConvert.DeserializeObject<ST_TICKET>(retJsonString);
                MergeItemStruct(pstTicket, StTicketTemp);
            }
            return retcode;
        }

        public static UInt32 FP3_VoidPayment(UInt32 hInt, UInt64 hTrx, UInt16 Index, ref ST_TICKET pstTicket, int TimeoutInMiliseconds)
        {
            byte[] szJsonTicket_Out = new byte[Defines.GMP_TICKET_BUFFER];

            UInt32 retcode = Json_FP3_VoidPayment(hInt, hTrx, Index, szJsonTicket_Out, szJsonTicket_Out.Length, TimeoutInMiliseconds);
            if (retcode == 0)
            {
                string retJsonString = GMP_Tools.GetStringFromBytes(szJsonTicket_Out);
                ST_TICKET StTicketTemp = new ST_TICKET();
                StTicketTemp = JsonConvert.DeserializeObject<ST_TICKET>(retJsonString);
                MergeItemStruct(pstTicket, StTicketTemp);
            }
            return retcode;
        }

        public static UInt32 FP3_VoidItem(UInt32 hInt, UInt64 hTrx, UInt16 Index, UInt64 ItemCount, byte ItemCountPrecision, ref ST_TICKET pstTicket, int TimeoutInMiliseconds)
        {
            byte[] szJsonTicket_Out = new byte[Defines.GMP_TICKET_BUFFER];

            UInt32 retcode = Json_FP3_VoidItem(hInt, hTrx, Index, ItemCount, ItemCountPrecision, szJsonTicket_Out, szJsonTicket_Out.Length, TimeoutInMiliseconds);
            if (retcode == 0)
            {
                string retJsonString = GMP_Tools.GetStringFromBytes(szJsonTicket_Out);
                ST_TICKET StTicketTemp = new ST_TICKET();
                StTicketTemp = JsonConvert.DeserializeObject<ST_TICKET>(retJsonString);
                MergeItemStruct(pstTicket, StTicketTemp);
            }
            return retcode;
        }

        public static UInt32 FP3_FunctionGetUniqueIdList(UInt32 hInt, ref ST_UNIQUE_ID[] pStUniqueIdList, UInt16 MaxNumberOfitems, UInt16 IndexOfitemsToStart, ref UInt16 pTotalNumberOfItems, ref UInt16 pNumberOfItemsInThis, int TimeoutInMiliseconds)
        {
            string szJsonUniqueIdList = JsonConvert.SerializeObject(pStUniqueIdList);
            byte[] szJsonUniqueIdList_In = GMP_Tools.GetBytesFromString(szJsonUniqueIdList);
            byte[] szJsonUniqueIdList_Out = new byte[Defines.STANDART_BUFFER];

            UInt32 retcode = Json_FP3_FunctionGetUniqueIdList(hInt, szJsonUniqueIdList_In, szJsonUniqueIdList_Out, szJsonUniqueIdList_Out.Length, MaxNumberOfitems, IndexOfitemsToStart, ref pTotalNumberOfItems, ref pNumberOfItemsInThis, TimeoutInMiliseconds);
            if (retcode == 0)
            {
                string retJsonString = GMP_Tools.GetStringFromBytes(szJsonUniqueIdList_Out);
                pStUniqueIdList = JsonConvert.DeserializeObject<ST_UNIQUE_ID[]>(retJsonString);
            }
            return retcode;
        }

        public static UInt32 FP3_FunctionReadCard(UInt32 hInt, int CardReaderTypes, ref ST_CARD_INFO pStCardInfo, int TimeoutInMiliseconds)
        {
            string szJsonCardInfo = JsonConvert.SerializeObject(pStCardInfo);
            byte[] szJsonCardInfo_In = GMP_Tools.GetBytesFromString(szJsonCardInfo);
            byte[] szJsonCardInfo_Out = new byte[Defines.STANDART_BUFFER];

            UInt32 retcode = Json_FP3_FunctionReadCard(hInt, CardReaderTypes, szJsonCardInfo_In, szJsonCardInfo_Out, szJsonCardInfo_Out.Length, TimeoutInMiliseconds);
            if (retcode == 0)
            {
                string retJsonString = GMP_Tools.GetStringFromBytes(szJsonCardInfo_Out);
                pStCardInfo = JsonConvert.DeserializeObject<ST_CARD_INFO>(retJsonString);
            }
            return retcode;
        }

        public static UInt32 FP3_GetCashierTable(UInt32 hInt, ref int pNumberOfTotalRecords, ref int pNumberOfTotalRecordsReceived, ref ST_CASHIER[] pStCashier, int NumberOfRecordsRequested, ref short pActiveCashier)
        {
            string szJsonCashier = JsonConvert.SerializeObject(pStCashier);
            byte[] szJsonCashier_In = GMP_Tools.GetBytesFromString(szJsonCashier);
            byte[] szJsonCashier_Out = new byte[Defines.GMP_TICKET_BUFFER];

            UInt32 retcode = Json_FP3_GetCashierTable(hInt, ref pNumberOfTotalRecords, ref pNumberOfTotalRecordsReceived, szJsonCashier_In, szJsonCashier_Out, szJsonCashier_Out.Length, NumberOfRecordsRequested, ref pActiveCashier);
            if (retcode == 0)
            {
                string retJsonString = GMP_Tools.GetStringFromBytes(szJsonCashier_Out);
                pStCashier = JsonConvert.DeserializeObject<ST_CASHIER[]>(retJsonString);
            }
            return retcode;
        }

        public static UInt32 FP3_Echo(UInt32 hInt, ref ST_ECHO pStEcho, int TimeoutInMiliseconds)
        {
            byte[] szJsonEcho_Out = new byte[Defines.GMP_TICKET_BUFFER];

            UInt32 retcode = Json_FP3_Echo(hInt, szJsonEcho_Out, szJsonEcho_Out.Length, TimeoutInMiliseconds);
            if (retcode == 0)
            {
                string retJsonString = GMP_Tools.GetStringFromBytes(szJsonEcho_Out);
                pStEcho = JsonConvert.DeserializeObject<ST_ECHO>(retJsonString);
            }
            return retcode;
        }

        public static UInt32 FP3_StartPairingInit(UInt32 hInt, ref ST_GMP_PAIR pStPair, ref ST_GMP_PAIR_RESP pStPairResp, int TimeoutInMiliseconds = Defines.TIMEOUT_DEFAULT)
        {
            string szJsonPairing = JsonConvert.SerializeObject(pStPair);
            byte[] szJsonPairing_In = GMP_Tools.GetBytesFromString(szJsonPairing);
            byte[] szJsonPairing_Out = new byte[Defines.GMP_TICKET_BUFFER];

            UInt32 retcode = Json_FP3_StartPairingInit(hInt, szJsonPairing_In, szJsonPairing_Out, szJsonPairing_Out.Length);
            if (retcode == 0)
            {
                string retJsonString = GMP_Tools.GetStringFromBytes(szJsonPairing_Out);
                pStPairResp = JsonConvert.DeserializeObject<ST_GMP_PAIR_RESP>(retJsonString);
            }
            return retcode;
        }

        public static UInt32 FP3_GetPaymentApplicationInfo(UInt32 hInt, ref byte pNumberOfTotalRecords, ref byte pNumberOfTotalRecordsReceived, ref ST_PAYMENT_APPLICATION_INFO[] pStAppInfo, byte NumberOfRecordsRequested)
        {
            string szJsonAppInfo = JsonConvert.SerializeObject(pStAppInfo);
            byte[] szJsonAppInfo_In = GMP_Tools.GetBytesFromString(szJsonAppInfo);
            byte[] szJsonAppInfo_Out = new byte[Defines.STANDART_BUFFER];

            UInt32 retcode = Json_FP3_GetPaymentApplicationInfo(hInt, ref pNumberOfTotalRecords, ref pNumberOfTotalRecordsReceived, szJsonAppInfo_In, szJsonAppInfo_Out, szJsonAppInfo_Out.Length, NumberOfRecordsRequested);
            if (retcode == 0)
            {
                string retJsonString = GMP_Tools.GetStringFromBytes(szJsonAppInfo_Out);
                pStAppInfo = JsonConvert.DeserializeObject<ST_PAYMENT_APPLICATION_INFO[]>(retJsonString);
            }
            return retcode;
        }

        public static UInt32 FP3_SetInvoice(UInt32 hInt, UInt64 hTrx, ref ST_INVIOCE_INFO pStInvoiceInfo, ref ST_TICKET pstTicket, int TimeoutInMiliseconds)
        {
            byte[] szJsonTicket_Out = new byte[Defines.GMP_TICKET_BUFFER];
            string szJsonInvoiceInfo = JsonConvert.SerializeObject(pStInvoiceInfo);
            byte[] szJsonInvoiceInfo_In = GMP_Tools.GetBytesFromString(szJsonInvoiceInfo);
            byte[] szJsonInvoiceInfo_Out = new byte[Defines.GMP_TICKET_BUFFER];

            UInt32 retcode = Json_FP3_SetInvoice(hInt, hTrx, szJsonInvoiceInfo_In, szJsonInvoiceInfo_Out, szJsonInvoiceInfo_Out.Length, szJsonTicket_Out, szJsonTicket_Out.Length, TimeoutInMiliseconds);
            if (retcode == 0)
            {
                string retJsonString = GMP_Tools.GetStringFromBytes(szJsonInvoiceInfo_Out);
                pStInvoiceInfo = JsonConvert.DeserializeObject<ST_INVIOCE_INFO>(retJsonString);
                retJsonString = GMP_Tools.GetStringFromBytes(szJsonTicket_Out);
                pstTicket = JsonConvert.DeserializeObject<ST_TICKET>(retJsonString);
            }
            return retcode;
        }

        public static UInt32 FP3_SetOnlineInvoice(UInt32 hInt, UInt64 hTrx, ref ST_ONLINE_INVIOCE_INFO pStOnlineInvoiceInfo, ref ST_TICKET pstTicket, int TimeoutInMiliseconds)
        {
            byte[] szJsonTicket_Out = new byte[Defines.GMP_TICKET_BUFFER];
            string szJsonInvoiceInfo = JsonConvert.SerializeObject(pStOnlineInvoiceInfo);
            byte[] szJsonInvoiceInfo_In = GMP_Tools.GetBytesFromString(szJsonInvoiceInfo);

            UInt32 retcode = Json_FP3_SetOnlineInvoice(hInt, hTrx, szJsonInvoiceInfo_In, szJsonTicket_Out, szJsonTicket_Out.Length, TimeoutInMiliseconds);

            string retJsonString = GMP_Tools.GetStringFromBytes(szJsonTicket_Out);
            pstTicket = JsonConvert.DeserializeObject<ST_TICKET>(retJsonString);

            return retcode;
        }
        public static UInt32 FP3_SetTaxFreeInfo(UInt32 hInt, UInt64 hTrx, ref ST_TAXFREE_INFO pStTaxFreeInfo, ref ST_TICKET pstTicket, int TimeoutInMiliseconds)
        {
            byte[] szJsonTicket_Out = new byte[Defines.GMP_TICKET_BUFFER];
            string szJsonTaxFreeInfo = JsonConvert.SerializeObject(pStTaxFreeInfo);
            byte[] szJsonTaxFreeInfo_In = GMP_Tools.GetBytesFromString(szJsonTaxFreeInfo);

            UInt32 retcode = Json_FP3_SetTaxFreeInfo(hInt, hTrx, szJsonTaxFreeInfo_In, szJsonTicket_Out, szJsonTicket_Out.Length, TimeoutInMiliseconds);

            string retJsonString = GMP_Tools.GetStringFromBytes(szJsonTicket_Out);
            pstTicket = JsonConvert.DeserializeObject<ST_TICKET>(retJsonString);

            return retcode;
        }

        public static int prepare_ItemSale(byte[] Buffer, int MaxSize, ref ST_ITEM pStItem)
        {
            string szJsonItem = JsonConvert.SerializeObject(pStItem);
            byte[] szJsonItem_In = GMP_Tools.GetBytesFromString(szJsonItem);
            byte[] szJsonItem_Out = new byte[Defines.GMP_TICKET_BUFFER];

            int retcode = Json_prepare_ItemSale(Buffer, MaxSize, szJsonItem_In, szJsonItem_Out, szJsonItem_Out.Length);
            if (retcode == 0)
            {
                string retJsonString = GMP_Tools.GetStringFromBytes(szJsonItem_Out);
                pStItem = JsonConvert.DeserializeObject<ST_ITEM>(retJsonString);
            }
            return retcode;
        }

        public static int prepare_Payment(byte[] Buffer, int MaxSize, ref ST_PAYMENT_REQUEST pStPaymentRequest)
        {
            string szJsonPayment = JsonConvert.SerializeObject(pStPaymentRequest);
            byte[] szJsonPayment_In = GMP_Tools.GetBytesFromString(szJsonPayment);
            byte[] szJsonPayment_Out = new byte[Defines.GMP_TICKET_BUFFER];

            int retcode = Json_prepare_Payment(Buffer, MaxSize, szJsonPayment_In, szJsonPayment_Out, szJsonPayment_Out.Length);

            string retJsonString = GMP_Tools.GetStringFromBytes(szJsonPayment_Out);
            pStPaymentRequest = JsonConvert.DeserializeObject<ST_PAYMENT_REQUEST>(retJsonString);

            return retcode;
        }

        public static UInt32 FP3_FunctionReports(UInt32 hInt, int functionFlags, ref ST_FUNCTION_PARAMETERS pStFunctionParameters, int TimeoutInMiliseconds)
        {
            string szJsonFunctionParameters = JsonConvert.SerializeObject(pStFunctionParameters);
            byte[] szJsonFunctionParameters_In = GMP_Tools.GetBytesFromString(szJsonFunctionParameters);
            byte[] szJsonFunctionParameters_Out = new byte[Defines.STANDART_BUFFER];

            UInt32 retcode = Json_FP3_FunctionReports(hInt, functionFlags, szJsonFunctionParameters_In, szJsonFunctionParameters_Out, szJsonFunctionParameters_Out.Length, TimeoutInMiliseconds);
            //if (retcode == 0)
            //{
            //    string retJsonString = GMP_Tools.SetEncoding(json_Out_stFunctionParameters);
            //    pstCardInfo = JsonConvert.DeserializeObject<ST_FUNCTION_PARAMETERS>(retJsonString);
            //}
            return retcode;
        }

        public static UInt32 FP3_FunctionReadZReport(UInt32 hInt, ref ST_FUNCTION_PARAMETERS pStFunctionParameters, ref ST_Z_REPORT pStZReport, int TimeoutInMiliseconds)
        {
            string szJsonFunctionParameters = JsonConvert.SerializeObject(pStFunctionParameters);
            byte[] szJsonFunctionParameters_In = GMP_Tools.GetBytesFromString(szJsonFunctionParameters);
            byte[] szJsonFunctionParameters_Out = new byte[Defines.GMP_TICKET_BUFFER];

            UInt32 retcode = Json_FP3_FunctionReadZReport(hInt, szJsonFunctionParameters_In, szJsonFunctionParameters_Out, szJsonFunctionParameters_Out.Length, TimeoutInMiliseconds);
            if (retcode == 0)
            {
                string retJsonString = GMP_Tools.GetStringFromBytes(szJsonFunctionParameters_Out);
                pStZReport = JsonConvert.DeserializeObject<ST_Z_REPORT>(retJsonString);
            }
            return retcode;
        }

        public static UInt32 FP3_FunctionReadDM_Report(UInt32 hInt, ref ST_FUNCTION_PARAMETERS pStFunctionParameters, ref ST_DM_REPORT pstDM_Report, int TimeoutInMiliseconds)
        {
            string szJsonFunctionParameters = JsonConvert.SerializeObject(pStFunctionParameters);
            byte[] szJsonFunctionParameters_In = GMP_Tools.GetBytesFromString(szJsonFunctionParameters);
            byte[] szJsonFunctionParameters_Out = new byte[Defines.GMP_TICKET_BUFFER];

            UInt32 retcode = Json_FP3_FunctionReadDM_Report(hInt, szJsonFunctionParameters_In, szJsonFunctionParameters_Out, szJsonFunctionParameters_Out.Length, TimeoutInMiliseconds);
            if (retcode == 0)
            {
                string retJsonString = GMP_Tools.GetStringFromBytes(szJsonFunctionParameters_Out);
                pstDM_Report = JsonConvert.DeserializeObject<ST_DM_REPORT>(retJsonString);
            }
            return retcode;
        }

        public static UInt32 FP3_FunctionPaymentCheck(UInt32 hInt, char[] uniqueId, ref ST_PAYMENT_RESPONSE paymentCheckResponse, int TimeoutInMiliseconds)
        {
            string szJsonCheckResponse = JsonConvert.SerializeObject(paymentCheckResponse);
            byte[] szJsonCheckResponse_In = Encoding.UTF8.GetBytes(uniqueId);
            byte[] szJsonCheckResponse_Out = new byte[Defines.GMP_TICKET_BUFFER];

            UInt32 retcode = Json_FP3_FunctionPaymentCheck(hInt, szJsonCheckResponse_In, szJsonCheckResponse_Out, szJsonCheckResponse_Out.Length, TimeoutInMiliseconds);
            if (retcode == 0)
            {
                string retJsonString = GMP_Tools.GetStringFromBytes(szJsonCheckResponse_Out);
                paymentCheckResponse = JsonConvert.DeserializeObject<ST_PAYMENT_RESPONSE>(retJsonString);
            }
            return retcode;
        }

        public static int prepare_SetInvoice(byte[] Buffer, int MaxSize, ref ST_INVIOCE_INFO pStInvoiceInfo)
        {
            string szJsonInvoiceInfo = JsonConvert.SerializeObject(pStInvoiceInfo);
            byte[] szJsonInvoiceInfo_In = GMP_Tools.GetBytesFromString(szJsonInvoiceInfo);
            byte[] szJsonInvoiceInfo_Out = new byte[Defines.GMP_TICKET_BUFFER];

            int retcode = Json_prepare_SetInvoice(Buffer, MaxSize, szJsonInvoiceInfo_In, szJsonInvoiceInfo_Out, szJsonInvoiceInfo_Out.Length);

            string retJsonString = GMP_Tools.GetStringFromBytes(szJsonInvoiceInfo_Out);
            pStInvoiceInfo = JsonConvert.DeserializeObject<ST_INVIOCE_INFO>(retJsonString);

            return retcode;
        }

        public static int prepare_SetOnlineInvoice(byte[] Buffer, int MaxSize, ref ST_ONLINE_INVIOCE_INFO pStInvoiceInfo)
        {
            string szJsonInvoiceInfo = JsonConvert.SerializeObject(pStInvoiceInfo);
            byte[] szJsonInvoiceInfo_In = GMP_Tools.GetBytesFromString(szJsonInvoiceInfo);

            int retcode = Json_prepare_SetOnlineInvoice(Buffer, MaxSize, szJsonInvoiceInfo_In);

            return retcode;
        }
        public static int prepare_SetTaxFreeInfo(byte[] Buffer, int MaxSize, ref ST_TAXFREE_INFO pStTaxFreeInfo)
        {
            string szJsonTaxFreeInfo = JsonConvert.SerializeObject(pStTaxFreeInfo);
            byte[] szJsonTaxFreeInfo_In = GMP_Tools.GetBytesFromString(szJsonTaxFreeInfo);

            int retcode = Json_prepare_SetTaxFreeInfo(Buffer, MaxSize, szJsonTaxFreeInfo_In);

            return retcode;
        }
        public static UInt32 parse_FiscalPrinter(ref  ST_MULTIPLE_RETURN_CODE[] pStReturnCodes, ref UInt16 pNumberOfreturnCodes, UInt32 RecvMsgId, byte[] RecvFullBuffer, UInt16 RecvFullLen, ref ST_TICKET pstTicket, int MaxNumberOfReturnCode, int MaxReturnCodeDataLen)
        {
            byte[] szJsonTicket_Out = new byte[Defines.GMP_TICKET_BUFFER];
            byte[] szJsonRetCodes_Out = new byte[Defines.GMP_TICKET_BUFFER];

            UInt32 retcode = Json_parse_FiscalPrinter(szJsonRetCodes_Out, szJsonRetCodes_Out.Length, ref pNumberOfreturnCodes, RecvMsgId, RecvFullBuffer, RecvFullLen, szJsonTicket_Out, szJsonTicket_Out.Length, MaxNumberOfReturnCode, MaxReturnCodeDataLen);
            if (retcode == 0)
            {
                string retJsonString = GMP_Tools.GetStringFromBytes(szJsonRetCodes_Out);
                pStReturnCodes = JsonConvert.DeserializeObject<ST_MULTIPLE_RETURN_CODE[]>(retJsonString);
                retJsonString = GMP_Tools.GetStringFromBytes(szJsonTicket_Out);
                pstTicket = JsonConvert.DeserializeObject<ST_TICKET>(retJsonString);
            }
            return retcode;
        }

        public static UInt32 parse_GetEcr(ref  ST_MULTIPLE_RETURN_CODE[] pStReturnCodes, ref int pNumberOfreturnCodes, UInt32 RecvMsgId, byte[] RecvFullBuffer, UInt16 RecvFullLen, int MaxNumberOfReturnCode, int MaxReturnCodeDataLen)
        {
            string szJsonRetCodes = JsonConvert.SerializeObject(pStReturnCodes);
            byte[] szJsonRetCodes_Out = new byte[Defines.GMP_TICKET_BUFFER];

            UInt32 retcode = Json_parse_GetEcr(szJsonRetCodes_Out, szJsonRetCodes_Out.Length, ref pNumberOfreturnCodes, RecvMsgId, RecvFullBuffer, RecvFullLen, MaxNumberOfReturnCode, MaxReturnCodeDataLen);
            if (retcode == 0)
            {
                string retJsonString = GMP_Tools.GetStringFromBytes(szJsonRetCodes_Out);
                pStReturnCodes = JsonConvert.DeserializeObject<ST_MULTIPLE_RETURN_CODE[]>(retJsonString);
            }
            return retcode;
        }

        public static int prepare_ReversePayment(byte[] Buffer, int MaxSize, ref ST_PAYMENT_REQUEST pStPaymentRequest, ushort NumberOfPaymentRequests)
        {
            string szJsonPayment = JsonConvert.SerializeObject(pStPaymentRequest);
            byte[] szJsonPayment_In = GMP_Tools.GetBytesFromString(szJsonPayment);
            byte[] szJsonPayment_Out = new byte[Defines.GMP_TICKET_BUFFER];

            int retcode = Json_prepare_ReversePayment(Buffer, MaxSize, szJsonPayment_In, szJsonPayment_Out, szJsonPayment_Out.Length, NumberOfPaymentRequests);
            if (retcode == 0)
            {
                string retJsonString = GMP_Tools.GetStringFromBytes(szJsonPayment_Out);
                pStPaymentRequest = JsonConvert.DeserializeObject<ST_PAYMENT_REQUEST>(retJsonString);
            }
            return retcode;
        }

        public static int prepare_Date(byte[] Buffer, int MaxSize, UInt32 TagId, byte[] Title, byte[] Text, byte[] Mask, ref ST_DATE_TIME pStValue, int TimeoutInMiliseconds)
        {
            string szJsonDate = JsonConvert.SerializeObject(pStValue);
            byte[] szJsonDate_In = GMP_Tools.GetBytesFromString(szJsonDate);
            byte[] szJsonDate_Out = new byte[Defines.GMP_TICKET_BUFFER];

            int retcode = Json_prepare_Date(Buffer, MaxSize, TagId, Title, Text, Mask, szJsonDate_In, szJsonDate_Out, szJsonDate_Out.Length, TimeoutInMiliseconds);
            if (retcode == 0)
            {
                string retJsonString = GMP_Tools.GetStringFromBytes(szJsonDate_Out);
                pStValue = JsonConvert.DeserializeObject<ST_DATE_TIME>(retJsonString);
            }
            return retcode;
        }

        public static int prepare_Condition(byte[] Buffer, int MaxSize, ref ST_CONDITIONAL_IF pStCondition)
        {
            string szJsonCondition = JsonConvert.SerializeObject(pStCondition);
            byte[] szJsonCondition_In = GMP_Tools.GetBytesFromString(szJsonCondition);
            byte[] szJsonCondition_Out = new byte[Defines.GMP_TICKET_BUFFER];

            int retcode = Json_prepare_Condition(Buffer, MaxSize, szJsonCondition_In, szJsonCondition_Out, szJsonCondition_Out.Length);
            if (retcode == 0)
            {
                string retJsonString = GMP_Tools.GetStringFromBytes(szJsonCondition_Out);
                pStCondition = JsonConvert.DeserializeObject<ST_CONDITIONAL_IF>(retJsonString);
            }
            return retcode;
        }

        public static UInt32 FP3_ReversePayment(UInt32 hInt, UInt64 hTrx, ref ST_PAYMENT_REQUEST pStPaymentRequest, short NumberOfPaymentRequests, ref ST_TICKET pstTicket, int TimeoutInMiliseconds) //TIMEOUT_CARD_TRANSACTIONS
        {
            byte[] szJsonTicket_Out = new byte[Defines.GMP_TICKET_BUFFER];
            string szJsonPaymentRequest = JsonConvert.SerializeObject(pStPaymentRequest);
            byte[] szJsonPaymentRequest_In = GMP_Tools.GetBytesFromString(szJsonPaymentRequest);
            byte[] szJsonPaymentRequest_Out = new byte[Defines.GMP_TICKET_BUFFER];

            UInt32 retcode = Json_FP3_ReversePayment(hInt, hTrx, szJsonPaymentRequest_In, szJsonPaymentRequest_Out, szJsonPaymentRequest_Out.Length, NumberOfPaymentRequests, szJsonTicket_Out, szJsonTicket_Out.Length, TimeoutInMiliseconds);
            if (retcode == 0)
            {
                string retJsonString = GMP_Tools.GetStringFromBytes(szJsonPaymentRequest_Out);
                pStPaymentRequest = JsonConvert.DeserializeObject<ST_PAYMENT_REQUEST>(retJsonString);
                retJsonString = GMP_Tools.GetStringFromBytes(szJsonTicket_Out);
                pstTicket = JsonConvert.DeserializeObject<ST_TICKET>(retJsonString);
            }
            return retcode;
        }

        public static int prepare_PrintUserMessage(byte[] Buffer, int MaxSize, ref ST_USER_MESSAGE[] pStUserMessage, ushort NumberOfMessage)
        {

            string szJsonUser = JsonConvert.SerializeObject(pStUserMessage);
            byte[] szJsonUser_In = GMP_Tools.GetBytesFromString(szJsonUser);
            byte[] szJsonUser_Out = new byte[Defines.GMP_TICKET_BUFFER];

            int retcode = Json_prepare_PrintUserMessage(Buffer, MaxSize, szJsonUser_In, szJsonUser_Out, szJsonUser_Out.Length, NumberOfMessage);
            if (retcode == 0)
            {
                string retJsonString = GMP_Tools.GetStringFromBytes(szJsonUser_Out);
                pStUserMessage = JsonConvert.DeserializeObject<ST_USER_MESSAGE[]>(retJsonString);
            }
            return retcode;
        }

        public static int prepare_PrintUserMessage_Ex(byte[] Buffer, int MaxSize, ref ST_USER_MESSAGE[] pStUserMessage, ushort NumberOfMessage)
        {

            string szJsonUser = JsonConvert.SerializeObject(pStUserMessage);
            byte[] szJsonUser_In = GMP_Tools.GetBytesFromString(szJsonUser);
            byte[] szJsonUser_Out = new byte[Defines.GMP_TICKET_BUFFER];

            int retcode = Json_prepare_PrintUserMessage_Ex(Buffer, MaxSize, szJsonUser_In, szJsonUser_Out, szJsonUser_Out.Length, NumberOfMessage);
            if (retcode == 0)
            {
                string retJsonString = GMP_Tools.GetStringFromBytes(szJsonUser_Out);
                pStUserMessage = JsonConvert.DeserializeObject<ST_USER_MESSAGE[]>(retJsonString);
            }
            return retcode;
        }

        public static UInt32 FP3_JumpToECR(UInt32 hInt, UInt64 hTrx, UInt64 JumpFlags, ref ST_TICKET pstTicket, int TimeoutInMiliseconds)
        {
            byte[] json_Out_stTicket = new byte[Defines.GMP_TICKET_BUFFER];

            UInt32 retcode = Json_FP3_JumpToECR(hInt, hTrx, JumpFlags, json_Out_stTicket, json_Out_stTicket.Length, TimeoutInMiliseconds);
            if (retcode == 0)
            {
                string retJsonString = GMP_Tools.GetStringFromBytes(json_Out_stTicket);
                ST_TICKET StTicketTemp = new ST_TICKET();
                StTicketTemp = JsonConvert.DeserializeObject<ST_TICKET>(retJsonString);
                MergeItemStruct(pstTicket, StTicketTemp);
            }
            return retcode;
        }

        public static UInt32 FP3_MultipleCommand(UInt32 hInt, ref UInt64 hTrx, ref ST_MULTIPLE_RETURN_CODE[] pReturnCodes, ref ushort IndexOfReturnCodes, byte[] SendBuffer, UInt16 SendBufferLen, ref ST_TICKET pstTicket, int TimeoutInMiliseconds)
        {
            byte[] szJsonTicket_Out = new byte[Defines.GMP_TICKET_BUFFER];
            byte[] szJsonRetCodes_Out = new byte[Defines.GMP_TICKET_BUFFER];

            UInt32 retcode = Json_FP3_MultipleCommand(hInt, ref hTrx, szJsonRetCodes_Out, szJsonRetCodes_Out.Length, ref IndexOfReturnCodes, SendBuffer, SendBufferLen, szJsonTicket_Out, szJsonTicket_Out.Length, TimeoutInMiliseconds);
            if (retcode == 0)
            {
                string retJsonString = GMP_Tools.GetStringFromBytes(szJsonRetCodes_Out);
                pReturnCodes = JsonConvert.DeserializeObject<ST_MULTIPLE_RETURN_CODE[]>(retJsonString);
                retJsonString = GMP_Tools.GetStringFromBytes(szJsonTicket_Out);
                pstTicket = JsonConvert.DeserializeObject<ST_TICKET>(retJsonString);
            }
            return retcode;
       }

        public static UInt32 FP3_SetTaxFree(UInt32 hInt, UInt64 hTrx, int szFlag, string szName, string szSurname, string szIdentificationNo, string szCity, string szCountry, ref ST_TICKET pstTicket, int TimeoutInMiliseconds)
        {
            byte[] json_Out_stTicket = new byte[Defines.GMP_TICKET_BUFFER];

            byte[] Name = GMP_Tools.GetBytesFromString(szName);
            byte[] Surname = GMP_Tools.GetBytesFromString(szSurname);
            byte[] IdentificationNo = GMP_Tools.GetBytesFromString(szIdentificationNo);
            byte[] City = GMP_Tools.GetBytesFromString(szCity);
            byte[] Country = GMP_Tools.GetBytesFromString(szCountry);

            UInt32 retcode = Json_FP3_SetTaxFree(hInt, hTrx, szFlag, Name, Surname, IdentificationNo, City, Country, json_Out_stTicket, json_Out_stTicket.Length, TimeoutInMiliseconds);
            if (retcode == 0)
            {
                string retJsonString = GMP_Tools.GetStringFromBytes(json_Out_stTicket);
                ST_TICKET StTicketTemp = new ST_TICKET();
                StTicketTemp = JsonConvert.DeserializeObject<ST_TICKET>(retJsonString);
                MergeItemStruct(pstTicket, StTicketTemp);
            }
            return retcode;
        }

        public static UInt32 FP3_SetParkingTicket(UInt32 hInt, UInt64 hTrx, string szCarIdentification, ref ST_TICKET pstTicket, int TimeoutInMiliseconds)
        {
            byte[] json_Out_stTicket = new byte[Defines.GMP_TICKET_BUFFER];
            byte[] CarIdentification = GMP_Tools.GetBytesFromString(szCarIdentification);

            UInt32 retcode = Json_FP3_SetParkingTicket(hInt, hTrx, CarIdentification, json_Out_stTicket, json_Out_stTicket.Length, TimeoutInMiliseconds);
            if (retcode == 0)
            {
                string retJsonString = GMP_Tools.GetStringFromBytes(json_Out_stTicket);
                ST_TICKET StTicketTemp = new ST_TICKET();
                StTicketTemp = JsonConvert.DeserializeObject<ST_TICKET>(retJsonString);
                MergeItemStruct(pstTicket, StTicketTemp);
            }
            return retcode;
        }

        public static UInt32 FP3_SetTaxFreeRefundAmount(UInt32 hInt, UInt64 hTrx, int RefundAmount, ushort RefundAmountCurrency, ref ST_TICKET pstTicket, int TimeoutInMiliseconds)
        {
            byte[] json_Out_stTicket = new byte[Defines.GMP_TICKET_BUFFER];

            UInt32 retcode = Json_FP3_SetTaxFreeRefundAmount(hInt, hTrx, RefundAmount, RefundAmountCurrency, json_Out_stTicket, json_Out_stTicket.Length, TimeoutInMiliseconds);
            if (retcode == 0)
            {
                string retJsonString = GMP_Tools.GetStringFromBytes(json_Out_stTicket);
                ST_TICKET StTicketTemp = new ST_TICKET();
                StTicketTemp = JsonConvert.DeserializeObject<ST_TICKET>(retJsonString);
                MergeItemStruct(pstTicket, StTicketTemp);
            }
            return retcode;
        }

        public static UInt32 FP3_LoyaltyCustomerQuery(UInt32 hInt, UInt64 hTrx, ref ST_LOYALTY_SERVICE_REQ pstLoyaltyServiceReq, ref ST_TICKET pstTicket, int TimeoutInMiliseconds)
        {
            byte[] szJsonTicket_Out = new byte[Defines.GMP_TICKET_BUFFER];
            string szJsonLoyaltyServiceReq = JsonConvert.SerializeObject(pstLoyaltyServiceReq);
            byte[] szJsonLoyaltyServiceReq_In = GMP_Tools.GetBytesFromString(szJsonLoyaltyServiceReq);
            byte[] szJsonLoyaltyServiceReq_Out = new byte[Defines.GMP_TICKET_BUFFER];

            UInt32 retcode = Json_FP3_LoyaltyCustomerQuery(hInt, hTrx, szJsonLoyaltyServiceReq_In, szJsonLoyaltyServiceReq_Out, szJsonLoyaltyServiceReq_Out.Length, szJsonTicket_Out, szJsonTicket_Out.Length, TimeoutInMiliseconds);
            if (retcode == 0)
            {
                string retJsonString = GMP_Tools.GetStringFromBytes(szJsonLoyaltyServiceReq_Out);
                pstLoyaltyServiceReq = JsonConvert.DeserializeObject<ST_LOYALTY_SERVICE_REQ>(retJsonString);
                retJsonString = GMP_Tools.GetStringFromBytes(szJsonTicket_Out);
                ST_TICKET StTicketTemp = new ST_TICKET();
                StTicketTemp = JsonConvert.DeserializeObject<ST_TICKET>(retJsonString);
                MergeItemStruct(pstTicket, StTicketTemp);
            }
            return retcode;
        }

        public static UInt32 FP3_FunctionChangeTicketHeader(UInt32 hInt, string szSupervisorPassword, ref ushort pNumberOfSpaceTotal, ref ushort pNumberOfSpaceUsed, ref ST_TICKET_HEADER pStTicketHeader, int TimeoutInMiliseconds)
        {
            string szJsonTicketHeader = JsonConvert.SerializeObject(pStTicketHeader);
            byte[] szJsonTicketHeader_In = GMP_Tools.GetBytesFromString(szJsonTicketHeader);
            byte[] szJsonTicketHeader_Out = new byte[Defines.GMP_TICKET_BUFFER];
            byte[] SupervisorPassword = GMP_Tools.GetBytesFromString(szSupervisorPassword);

            UInt32 retcode = Json_FP3_FunctionChangeTicketHeader(hInt, SupervisorPassword, ref pNumberOfSpaceTotal, ref pNumberOfSpaceUsed, szJsonTicketHeader_In, szJsonTicketHeader_Out, szJsonTicketHeader_Out.Length, TimeoutInMiliseconds);
            if (retcode == 0)
            {
                string retJsonString = GMP_Tools.GetStringFromBytes(szJsonTicketHeader_Out);
                pStTicketHeader = JsonConvert.DeserializeObject<ST_TICKET_HEADER>(retJsonString);
            }
            return retcode;
        }

        public static UInt32 FP3_GetTicketHeader(UInt32 hInt, ushort IndexOfHeader, ref ST_TICKET_HEADER pStTicketHeader, ref ushort pNumberOfSpaceTotal, int TimeoutInMiliseconds)
        {
            string szJsonTicketHeader = JsonConvert.SerializeObject(pStTicketHeader);
            byte[] szJsonTicketHeader_In = GMP_Tools.GetBytesFromString(szJsonTicketHeader);
            byte[] szJsonTicketHeader_Out = new byte[Defines.GMP_TICKET_BUFFER];

            UInt32 retcode = Json_FP3_GetTicketHeader(hInt, IndexOfHeader, szJsonTicketHeader_In, szJsonTicketHeader_Out, szJsonTicketHeader_Out.Length, ref pNumberOfSpaceTotal, TimeoutInMiliseconds);
            if (retcode == 0)
            {
                string retJsonString = GMP_Tools.GetStringFromBytes(szJsonTicketHeader_Out);
                pStTicketHeader = JsonConvert.DeserializeObject<ST_TICKET_HEADER>(retJsonString);
            }
            return retcode;
        }

        public static UInt32 FP3_GetOnlineInvoiceInfo(UInt32 hInt, byte[] szOnlineInvoiceInfo, int OnlineInvoiceIdLen, ref ST_ONLINE_INVIOCE_INFO pStOnlineInvoiceInfo, int TimeoutInMiliseconds)
        {
            byte[] szJsonOnlineInvoiceInfo_Out = new byte[Defines.GMP_TICKET_BUFFER];

            UInt32 retcode = Json_FP3_GetOnlineInvoiceInfo(hInt, szOnlineInvoiceInfo, OnlineInvoiceIdLen, szJsonOnlineInvoiceInfo_Out, szJsonOnlineInvoiceInfo_Out.Length, TimeoutInMiliseconds);
            if (retcode == 0)
            {
                string retJsonString = GMP_Tools.GetStringFromBytes(szJsonOnlineInvoiceInfo_Out);
                pStOnlineInvoiceInfo = JsonConvert.DeserializeObject<ST_ONLINE_INVIOCE_INFO>(retJsonString);
            }
            return retcode;
        }

        public static UInt32 FP3_Database_QueryColomnCaptions(UInt32 hInt, ref ST_DATABASE_RESULT pStDatabaseResult)
        {
            byte[] szJsonDatabaseResult_Out = new byte[Defines.GMP_TICKET_BUFFER];

            UInt32 retcode = Json_FP3_Database_QueryColomnCaptions(hInt, szJsonDatabaseResult_Out, szJsonDatabaseResult_Out.Length);
            if (retcode == 0 || retcode == Defines.SQLITE_DONE || retcode == Defines.SQLITE_ROW)
            {
                string retJsonString = GMP_Tools.GetStringFromBytes(szJsonDatabaseResult_Out);
                pStDatabaseResult = JsonConvert.DeserializeObject<ST_DATABASE_RESULT>(retJsonString);
            }
            return retcode;
        }

        public static UInt32 FP3_Database_QueryReadLine(UInt32 hInt, ushort NumberOfLinesRequested, ushort NumberOfColomnsRequested, ref ST_DATABASE_RESULT pStDatabaseResult)
        {
            byte[] szJsonDatabaseResult_Out = new byte[Defines.GMP_TICKET_BUFFER];

            UInt32 retcode = Json_FP3_Database_QueryReadLine(hInt, NumberOfLinesRequested, NumberOfColomnsRequested, szJsonDatabaseResult_Out, szJsonDatabaseResult_Out.Length);
            if (retcode == 0 || retcode == Defines.SQLITE_DONE || retcode == Defines.SQLITE_ROW)
            {
                string retJsonString = GMP_Tools.GetStringFromBytes(szJsonDatabaseResult_Out);
                pStDatabaseResult = JsonConvert.DeserializeObject<ST_DATABASE_RESULT>(retJsonString);
            }
            return retcode;
        }

        public static void FP3_Database_FreeStructure(UInt32 hInt, ref ST_DATABASE_RESULT pStDatabaseResult)
        {
            string szJsonDatabaseResult = JsonConvert.SerializeObject(pStDatabaseResult);
            byte[] szJsonDatabaseResult_In = GMP_Tools.GetBytesFromString(szJsonDatabaseResult);
            byte[] szJsonDatabaseResult_Out = new byte[Defines.GMP_TICKET_BUFFER];

            Json_FP3_Database_FreeStructure(hInt, szJsonDatabaseResult_In, szJsonDatabaseResult_Out, szJsonDatabaseResult_Out.Length);

            string retJsonString = GMP_Tools.GetStringFromBytes(szJsonDatabaseResult_Out);
            pStDatabaseResult = JsonConvert.DeserializeObject<ST_DATABASE_RESULT>(retJsonString);
        }

        public static UInt32 FP3_Database_Execute(UInt32 hInt, string szSqlWord, ref ST_DATABASE_RESULT pStDatabaseResult)
        {
            byte[] szJsonDatabaseResult_Out = new byte[Defines.GMP_TICKET_BUFFER];
            byte[] SqlWord = GMP_Tools.GetBytesFromString(szSqlWord);

            UInt32 retcode = Json_FP3_Database_Execute(hInt, SqlWord, szJsonDatabaseResult_Out, szJsonDatabaseResult_Out.Length);
            if (retcode == 0 || retcode == Defines.SQLITE_DONE || retcode == Defines.SQLITE_ROW)
            {
                string retJsonString = GMP_Tools.GetStringFromBytes(szJsonDatabaseResult_Out);
                pStDatabaseResult = JsonConvert.DeserializeObject<ST_DATABASE_RESULT>(retJsonString);
            }
            return retcode;
        }

        public static UInt32 FP3_GetPLU(UInt32 hInt, string szBarcode, ref ST_PLU_RECORD StPluRecord, ref ST_PLU_GROUP_RECORD[] StPluGroupRecord, int MaxNumberOfGroupRecords, int TimeoutInMiliseconds)
        {
            string szJsonPluRecord = JsonConvert.SerializeObject(StPluRecord);
            byte[] szJsonPluRecord_In = GMP_Tools.GetBytesFromString(szJsonPluRecord);
            byte[] szJsonPluRecord_Out = new byte[Defines.GMP_TICKET_BUFFER];
            string szJsonPluGroupRecord = JsonConvert.SerializeObject(StPluGroupRecord);
            byte[] szJsonPluGroupRecord_In = GMP_Tools.GetBytesFromString(szJsonPluGroupRecord);
            byte[] szJsonPluGroupRecord_Out = new byte[Defines.GMP_TICKET_BUFFER];
            byte[] Barcode = GMP_Tools.GetBytesFromString(szBarcode);

            UInt32 retcode = Json_FP3_GetPLU(hInt, Barcode, szJsonPluRecord_In, szJsonPluRecord_Out, szJsonPluRecord_Out.Length, szJsonPluGroupRecord_In, szJsonPluGroupRecord_Out, szJsonPluGroupRecord_Out.Length, MaxNumberOfGroupRecords, TimeoutInMiliseconds);

            if (retcode == 0)
            {
                string retJsonString = GMP_Tools.GetStringFromBytes(szJsonPluRecord_Out);
                StPluRecord = JsonConvert.DeserializeObject<ST_PLU_RECORD>(retJsonString);
                retJsonString = GMP_Tools.GetStringFromBytes(szJsonPluGroupRecord_Out);
                StPluGroupRecord = JsonConvert.DeserializeObject<ST_PLU_GROUP_RECORD[]>(retJsonString);
            }
            return retcode;
        }

        public static UInt32 FP3_GetVasApplicationInfo(UInt32 hInt, ref byte pNumberOfTotalRecords, ref byte pNumberOfTotalRecordsReceived, ref ST_PAYMENT_APPLICATION_INFO[] StPaymentAppInfo, UInt16 vasType)
        {
            byte[] szJsonPluGroupRecord_Out = new byte[Defines.GMP_TICKET_BUFFER];

            UInt32 retcode = Json_FP3_GetVasApplicationInfo(hInt, ref pNumberOfTotalRecords, ref pNumberOfTotalRecordsReceived, szJsonPluGroupRecord_Out, szJsonPluGroupRecord_Out.Length, vasType);

            if (retcode == 0)
            {
                string retJsonString = GMP_Tools.GetStringFromBytes(szJsonPluGroupRecord_Out);
                StPaymentAppInfo = JsonConvert.DeserializeObject<ST_PAYMENT_APPLICATION_INFO[]>(retJsonString);
            }
            return retcode;
        }

        public static UInt32 FP3_GetVasLoyaltyServiceInfo(UInt32 hInt, ref byte pNumberOfTotalRecords, ref byte pNumberOfTotalRecordsReceived, ref ST_LOYALTY_SERVICE_INFO[] StLoyaltyAppInfo, UInt16 VasAppId)
        {
            byte[] szJsonLoyaltyPaymentAppInfo_Out = new byte[Defines.GMP_TICKET_BUFFER];

            UInt32 retcode = Json_FP3_GetVasLoyaltyServiceInfo(hInt, ref pNumberOfTotalRecords, ref pNumberOfTotalRecordsReceived, szJsonLoyaltyPaymentAppInfo_Out, szJsonLoyaltyPaymentAppInfo_Out.Length, VasAppId);

            if (retcode == 0)
            {
                string retJsonString = GMP_Tools.GetStringFromBytes(szJsonLoyaltyPaymentAppInfo_Out);
                StLoyaltyAppInfo = JsonConvert.DeserializeObject<ST_LOYALTY_SERVICE_INFO[]>(retJsonString);
            }
            return retcode;
        }

        public static UInt32 FP3_FunctionEkuSeek(UInt32 hInt, ref ST_EKU_APPINF StEKUAppInfo, int TimeoutInMiliseconds)
        {
            string szJsonEKUAppInfo = JsonConvert.SerializeObject(StEKUAppInfo);
            byte[] szJsonEKUAppInfo_In = GMP_Tools.GetBytesFromString(szJsonEKUAppInfo);
            byte[] szJsonEKUAppInfo_Out = new byte[Defines.GMP_TICKET_BUFFER];

            UInt32 retcode = Json_FP3_FunctionEkuSeek(hInt, szJsonEKUAppInfo_In, szJsonEKUAppInfo_Out, szJsonEKUAppInfo_Out.Length, TimeoutInMiliseconds);

            if (retcode == 0)
            {
                string retJsonString = GMP_Tools.GetStringFromBytes(szJsonEKUAppInfo_Out);
                StEKUAppInfo = JsonConvert.DeserializeObject<ST_EKU_APPINF>(retJsonString);
            }
            return retcode;
        }

        public static UInt32 FP3_FileSystem_DirListFiles(UInt32 hInt, string szDirName, ref ST_FILE[] StFile, short MaxNumberOfFiles, ref short pNumberOfFiles)
        {
            string szJsonFile = JsonConvert.SerializeObject(StFile);
            byte[] szJsonFile_In = GMP_Tools.GetBytesFromString(szJsonFile);
            byte[] szJsonFile_Out = new byte[Defines.GMP_TICKET_BUFFER];
            byte[] DirName = GMP_Tools.GetBytesFromString(szDirName);

            UInt32 retcode = Json_FP3_FileSystem_DirListFiles(hInt, DirName, szJsonFile_In, szJsonFile_Out, szJsonFile_Out.Length, MaxNumberOfFiles, ref pNumberOfFiles);

            if (retcode == 0)
            {
                string retJsonString = GMP_Tools.GetStringFromBytes(szJsonFile_Out);
                StFile = JsonConvert.DeserializeObject<ST_FILE[]>(retJsonString);
            }
            return retcode;
        }

        public static UInt32 FP3_FunctionEkuReadHeader(UInt32 hInt, short Index, ref ST_EKU_HEADER StEkuHeader, int TimeoutInMiliseconds)
        {
            string szJsonEkuHeader = JsonConvert.SerializeObject(StEkuHeader);
            byte[] szJsonEkuHeader_In = GMP_Tools.GetBytesFromString(szJsonEkuHeader);
            byte[] szJsonEkuHeader_Out = new byte[Defines.GMP_TICKET_BUFFER];

            UInt32 retcode = Json_FP3_FunctionEkuReadHeader(hInt, Index, szJsonEkuHeader_In, szJsonEkuHeader_Out, szJsonEkuHeader_Out.Length, TimeoutInMiliseconds);

            if (retcode == 0)
            {
                string retJsonString = GMP_Tools.GetStringFromBytes(szJsonEkuHeader_Out);
                StEkuHeader = JsonConvert.DeserializeObject<ST_EKU_HEADER>(retJsonString);
            }
            return retcode;
        }

        public static UInt32 FP3_FunctionEkuReadData(UInt32 hInt, ref UInt16 pEkuDataBufferReceivedLen, ref ST_EKU_APPINF StEKUAppInfo, int TimeoutInMiliseconds)
        {
            string szJsonEKUAppInfo = JsonConvert.SerializeObject(StEKUAppInfo);
            byte[] szJsonEKUAppInfo_In = GMP_Tools.GetBytesFromString(szJsonEKUAppInfo);
            byte[] szJsonEKUAppInfo_Out = new byte[Defines.GMP_TICKET_BUFFER];

            UInt32 retcode = Json_FP3_FunctionEkuReadData(hInt, ref pEkuDataBufferReceivedLen, szJsonEKUAppInfo_In, szJsonEKUAppInfo_Out, szJsonEKUAppInfo_Out.Length, TimeoutInMiliseconds);

            if (retcode == 0)
            {
                string retJsonString = GMP_Tools.GetStringFromBytes(szJsonEKUAppInfo_Out);
                StEKUAppInfo = JsonConvert.DeserializeObject<ST_EKU_APPINF>(retJsonString);
            }
            return retcode;
        }

        public static UInt32 FP3_FunctionEkuReadInfo(UInt32 hInt, UInt16 EkuAccessFunction, ref ST_EKU_MODULE_INFO StEkuModuleInfo, int TimeoutInMiliseconds)
        {
            string szJsonEkuModuleInfo = JsonConvert.SerializeObject(StEkuModuleInfo);
            byte[] szJsonEkuModuleInfo_In = GMP_Tools.GetBytesFromString(szJsonEkuModuleInfo);
            byte[] szJsonEkuModuleInfo_Out = new byte[Defines.GMP_TICKET_BUFFER];

            UInt32 retcode = Json_FP3_FunctionEkuReadInfo(hInt, EkuAccessFunction, szJsonEkuModuleInfo_In, szJsonEkuModuleInfo_Out, szJsonEkuModuleInfo_Out.Length, TimeoutInMiliseconds);

            if (retcode == 0)
            {
                string retJsonString = GMP_Tools.GetStringFromBytes(szJsonEkuModuleInfo_Out);
                StEkuModuleInfo = JsonConvert.DeserializeObject<ST_EKU_MODULE_INFO>(retJsonString);
            }
            return retcode;
        }
        
        public static UInt32 FP3_FunctionModuleReadInfo(UInt32 hInt, int AccessFunction, ref ST_MODULE_USAGE_INFO StModuleUsageInfo, int TimeoutInMiliseconds)
        {
            string szJsonModuleUsageInfo = JsonConvert.SerializeObject(StModuleUsageInfo);
            byte[] szJsonModuleUsageInfo_In = GMP_Tools.GetBytesFromString(szJsonModuleUsageInfo);
            byte[] szJsonModuleUsageInfo_Out = new byte[Defines.GMP_TICKET_BUFFER];

            UInt32 retcode = Json_FP3_FunctionModuleReadInfo(hInt, AccessFunction, szJsonModuleUsageInfo_In, szJsonModuleUsageInfo_Out, szJsonModuleUsageInfo_Out.Length, TimeoutInMiliseconds);

            if (retcode == 0)
            {
                string retJsonString = GMP_Tools.GetStringFromBytes(szJsonModuleUsageInfo_Out);
                StModuleUsageInfo = JsonConvert.DeserializeObject<ST_MODULE_USAGE_INFO>(retJsonString);
            }
            return retcode;
        }

        public static UInt32 FP3_FunctionBankingRefund(UInt32 hInt, ref ST_PAYMENT_REQUEST StReversePayment, int TimeoutInMiliseconds)
        {
            string szJsonPaymentRequest = JsonConvert.SerializeObject(StReversePayment);
            byte[] szJsonPaymentRequest_In = GMP_Tools.GetBytesFromString(szJsonPaymentRequest);
            byte[] szJsonPaymentRequest_Out = new byte[Defines.GMP_TICKET_BUFFER];

            UInt32 retcode = Json_FP3_FunctionBankingRefund(hInt, szJsonPaymentRequest_In, szJsonPaymentRequest_Out, szJsonPaymentRequest_Out.Length, TimeoutInMiliseconds);
            if (retcode == 0)
            {
                string retJsonString = GMP_Tools.GetStringFromBytes(szJsonPaymentRequest_Out);
                StReversePayment = JsonConvert.DeserializeObject<ST_PAYMENT_REQUEST>(retJsonString);
            }
            return retcode;
        }

        public static UInt32 FP3_FunctionBankingRefundExt(UInt32 hInt, ref ST_PAYMENT_REQUEST StReversePayment,ref ST_PAYMENT_RESPONSE stReverseResponse,int TimeoutInMiliseconds)
        {
            string szJsonPaymentRequest = JsonConvert.SerializeObject(StReversePayment);
            byte[] szJsonPaymentRequest_In = GMP_Tools.GetBytesFromString(szJsonPaymentRequest);
            byte[] szJsonPaymentRequest_Out = new byte[Defines.GMP_TICKET_BUFFER];
            byte[] stJsonReverseResponse = new byte[Defines.GMP_TICKET_BUFFER];

            UInt32 retcode = Json_FP3_FunctionBankingRefundExt(hInt, szJsonPaymentRequest_In, szJsonPaymentRequest_Out, szJsonPaymentRequest_Out.Length, stJsonReverseResponse, stJsonReverseResponse.Length, TimeoutInMiliseconds);
            if (retcode == 0)
            {
                string retJsonString = GMP_Tools.GetStringFromBytes(szJsonPaymentRequest_Out);
                StReversePayment = JsonConvert.DeserializeObject<ST_PAYMENT_REQUEST>(retJsonString);
            }

            string retJsonStringResponse = GMP_Tools.GetStringFromBytes(stJsonReverseResponse);
            stReverseResponse = JsonConvert.DeserializeObject<ST_PAYMENT_RESPONSE>(retJsonStringResponse);
            return retcode;
        }

        public static UInt32 FP3_FunctionBankingBatch(UInt32 hInt, ushort BkmId, ref ushort pNumberOfBankResponse, ref ST_MULTIPLE_BANK_RESPONSE[] StMultipleBankResponse, int TimeoutInMiliseconds)
        {
            byte[] szJsonPaymentRequestMultipleBankResponse_Out = new byte[Defines.GMP_TICKET_BUFFER];

            UInt32 retcode = Json_FP3_FunctionBankingBatch(hInt, BkmId, ref pNumberOfBankResponse, szJsonPaymentRequestMultipleBankResponse_Out, szJsonPaymentRequestMultipleBankResponse_Out.Length, TimeoutInMiliseconds);
            if (retcode == 0)
            {
                string retJsonString = GMP_Tools.GetStringFromBytes(szJsonPaymentRequestMultipleBankResponse_Out);
                StMultipleBankResponse = JsonConvert.DeserializeObject<ST_MULTIPLE_BANK_RESPONSE[]>(retJsonString);
            }
            return retcode;
        }
        
        public static UInt32 FP3_FunctionTransactionInquiry(UInt32 hInt, ref ST_TRANS_INQUIRY stTransInquiry, int TimeoutInMiliseconds)
        {
            string szJson = JsonConvert.SerializeObject(stTransInquiry);
            byte[] szJsonIn = GMP_Tools.GetBytesFromString(szJson);
            byte[] szJsonOut = new byte[Defines.STANDART_BUFFER];

            UInt32 retcode = Json_FP3_FunctionTransactionInquiry(hInt, szJsonIn, szJsonOut, szJsonOut.Length, TimeoutInMiliseconds);
            if (retcode == 0)
            {
                string retJsonString = GMP_Tools.GetStringFromBytes(szJsonOut);
                stTransInquiry = JsonConvert.DeserializeObject<ST_TRANS_INQUIRY>(retJsonString);
            }
            return retcode;
        }
    }

    class GMPSmartDLL
    {
        [DllImport("GmpSmartDLL.dll", EntryPoint = "GenerateUniqueID", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 GenerateUniqueID(byte[] szPath);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "FP3_RemoveInterfaceByID", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 FP3_RemoveInterfaceByID(string InterfaceId);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "SetXmlFilePath", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 SetXmlFilePath(string szPath);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "GetXmlFilePath", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 GetXmlFilePath(byte[] szPath);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "FP3_FunctionCreateUniqueId", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 FP3_FunctionCreateUniqueId(UInt32 hInt, byte[] UniqueId, int timeoutInMiliseconds);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "FP3_GetInterfaceHandleList", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 FP3_GetInterfaceHandleList(UInt32[] phInt, UInt32 Max);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "FP3_GetInterfaceID", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 FP3_GetInterfaceID(UInt32 hInt, byte[] szID, UInt32 Max);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "FP3_GetInterfaceHandleByID", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 FP3_GetInterfaceHandleByID(ref UInt32 phInt, byte[] szID);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "FP3_FunctionCashierLogin", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 FP3_FunctionCashierLogin_WE(UInt32 hInt, int CashierIndex, byte[] szCashierPassword); // Without Encoding

        [DllImport("GmpSmartDLL.dll", EntryPoint = "FP3_FunctionAddCashier", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 FP3_FunctionAddCashier_WE(UInt32 hInt, ushort CashierIndex, byte[] szCashierName, byte[] szCashierPassword, byte[] szSupervisorPassword); // Without Encoding

        [DllImport("GmpSmartDLL.dll", EntryPoint = "SetIniFilePath", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SetIniFilePath_WE(byte[] szPath); // Without Encoding

        [DllImport("GmpSmartDLL.dll", EntryPoint = "FP3_Database_Open", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 FP3_Database_Open_WE(UInt32 hInt, byte[] szName); // Without Encoding

        [DllImport("GmpSmartDLL.dll", EntryPoint = "FP3_FileSystem_DirChange", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 FP3_FileSystem_DirChange_WE(UInt32 hInt, byte[] szDirName); // Without Encoding

        [DllImport("GmpSmartDLL.dll", EntryPoint = "FP3_FileSystem_FileRead", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 FP3_FileSystem_FileRead_WE(UInt32 hInt, byte[] szFileName, int Offset, int Whence, byte[] Buffer, int MaxLen, ref short pReadLen); // Without Encoding

        [DllImport("GmpSmartDLL.dll", EntryPoint = "FP3_SetPluDatabaseInfo", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 FP3_SetPluDatabaseInfo_WE(UInt32 hInt, byte[] szPluDbName, short szPluDbType); // Without Encoding

        [DllImport("GmpSmartDLL.dll", EntryPoint = "Database_Query", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 Database_Query_WE(byte[] szSqlWord, ref ushort pNumberOfColomns);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "FP3_Database_Query", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 FP3_Database_Query_WE(UInt32 hInt, byte[] szSqlWord, ref ushort pNumberOfColomns);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "prepare_SetTaxFree", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern int prepare_SetTaxFree_WE(byte[] Buffer, int MaxSize, int Flag, byte[] szName, byte[] szSurname, byte[] szIdentificationNo, byte[] szCity, byte[] szCountry);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "prepare_KasaAvans", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern int prepare_KasaAvans(byte[] Buffer, int MaxSize, int Amount, byte[] pCustomerName, byte[] pTckn, byte[] pVkn);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "GetTagName", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern byte[] GetTagName(UInt32 Tag);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "GetErrorMessage", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern void GetErrorMessage(UInt32 ErrorCode, byte[] Buffer);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "FP3_DisplayPaymentSummary", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern int FP3_DisplayPaymentSummary(UInt32 hInt, UInt64 hTrx, int TimeoutInMiliseconds);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "FP3_FunctionCashierLogout", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern int FP3_FunctionCashierLogout(UInt32 hInt);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "FiscalPrinter_GetHandle", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern ulong FiscalPrinter_GetHandle();

        [DllImport("GmpSmartDLL.dll", EntryPoint = "FiscalPrinter_ResetHandle", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern void FiscalPrinter_ResetHandle();

        [DllImport("GmpSmartDLL.dll", EntryPoint = "FP3_TicketHeader", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 FP3_TicketHeader(UInt32 hInt, UInt64 hTrx, TTicketType TicketType, int TimeoutInMiliseconds);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "FP3_PrintTotalsAndPayments", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 FP3_PrintTotalsAndPayments(UInt32 hInt, UInt64 hTrx, int TimeoutInMiliseconds);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "FP3_PrintBeforeMF", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 FP3_PrintBeforeMF(UInt32 hInt, UInt64 hTrx, int TimeoutInMiliseconds);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "FP3_PrintMF", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 FP3_PrintMF(UInt32 hInt, UInt64 hTrx, int TimeoutInMiliseconds);  //TIMEOUT_PRINT_MF

        [DllImport("GmpSmartDLL.dll", EntryPoint = "FP3_Ping", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 FP3_Ping(UInt32 hInt, int TimeoutInMiliseconds);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "FP3_Start", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 FP3_Start(UInt32 hInt, ref UInt64 hTrx, byte IsBackground, byte[] pUniqueId, int LengthOfUniqueId, byte[] pUniqueIdSign, int LengthOfUniqueIdSign, byte[] pUserData, int LengthOfUserData, int TimeoutInMiliseconds);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "FP3_Close", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 FP3_Close(UInt32 hInt, UInt64 hTrx, int TimeoutInMiliseconds);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "FP3_FunctionEcrParameter", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 FP3_FunctionEcrParameter(UInt32 hInt, int TimeoutInMiliseconds);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "FP3_FunctionBankingParameter", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 FP3_FunctionBankingParameter(UInt32 hInt, int TimeoutInMiliseconds);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "FP3_FunctionOpenDrawer", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 FP3_FunctionOpenDrawer(UInt32 hInt);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "FP3_GetCurrentFiscalCounters", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 FP3_GetCurrentFiscalCounters(UInt32 hInt, ref ushort pZNo, ref ushort pFNo, ref ushort pEKUNo);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "Database_GetHandle", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 Database_GetHandle();

        [DllImport("GmpSmartDLL.dll", EntryPoint = "FP3_Database_GetHandle", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 FP3_Database_GetHandle(UInt32 hInt);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "FP3_Database_Close", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 FP3_Database_Close(UInt32 hInt);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "FP3_Database_QueryReset", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 FP3_Database_QueryReset(UInt32 hInt);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "FP3_Database_QueryFinish", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 FP3_Database_QueryFinish(UInt32 hInt);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "FP3_FunctionEkuPing", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 FP3_FunctionEkuPing(UInt32 hInt, int TimeoutInMiliseconds);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "FP3_GetTlvData", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 FP3_GetTlvData(UInt32 hInt, int Tag, byte[] pData, short MaxBufferLen, ref short pDataLen);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "FP3_FileSystem_FileRemove", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 FP3_FileSystem_FileRemove(UInt32 hInt, byte[] pFileName);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "FP3_FileSystem_FileRename", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 FP3_FileSystem_FileRename(UInt32 hInt, byte[] pFileNameOld, byte[] pFileNameNew);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "FP3_FileSystem_FileWrite", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 FP3_FileSystem_FileWrite(UInt32 hInt, byte[] pFileName, int Offset, int Whence, byte[] Buffer, short Len);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "GMP_GetDllVersion", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 GMP_GetDllVersion(byte[] pVersion);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "FP3_GetPluDatabaseInfo", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 FP3_GetPluDatabaseInfo(UInt32 hInt, byte[] pPluDbName, ref short pPluDbType, ref uint pPluDbSize, ref uint pPluDbGrupsLastModified, ref uint pPluDbItemsLastModified);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "gmpReadTLVlen_HL", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt16 gmpReadTLVlen_HL(ref ushort pLen, byte[] pPtr, ushort PtrLen);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "gmpReadTLVtag_HL", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt16 gmpReadTLVtag(ref uint pTag, byte[] pPtr, ushort PtrLen);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "gmpTlvSetLen", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt16 gmpTlvSetLen(byte[] pMsg, ushort TlvLen);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "gmpSetTLV_HL", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt16 gmpSetTLV_HL(byte[] pMsg, int pMsgLen, uint Tag, byte[] pdata, ushort dataLen);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "gmpSetTLVbcd", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt16 gmpSetTLVbcd(byte[] pMsg, uint Tag, uint Data, ushort BcdLen);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "gmpSearchTLV", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern int gmpSearchTLV(uint Tag, int Ocurience, byte[] RecvBuffer, ushort RecvBufferLen, byte[] pData, ushort DataMaxLen);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "gmpSearchTLVbcd_8", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern int gmpSearchTLVbcd_8(uint Tag, int Ocurience, byte[] RecvBuffer, ushort RecvBufferLen, byte[] pData, byte BcdLen);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "gmpSearchTLVbcd_16", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern int gmpSearchTLVbcd_16(uint Tag, int Ocurience, byte[] RecvBuffer, ushort RecvBufferLen, byte[] pData, byte BcdLen);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "gmpSearchTLVbcd_32", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern int gmpSearchTLVbcd_32(uint Tag, int Ocurience, byte[] RecvBuffer, ushort RecvBufferLen, byte[] pData, byte BcdLen);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "gmpSearchTLVbcd_64", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern int gmpSearchTLVbcd_64(uint Tag, int Ocurience, byte[] RecvBuffer, ushort RecvBufferLen, byte[] pData, byte BcdLen);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "prepare_Start", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern int prepare_Start(byte[] Buffer, int MaxSize, byte[] pUniqueId, int LengthOfUniqueId, byte[] pUniqueIdSign, int LengthOfUniqueIdSign, byte[] pUserData, int LengthOfUserData);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "prepare_TicketHeader", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern int prepare_TicketHeader(byte[] Buffer, int MaxSize, TTicketType TicketType);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "prepare_Close", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern int prepare_Close(byte[] Buffer, int MaxSize);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "prepare_OptionFlags", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern int prepare_OptionFlags(byte[] Buffer, int MaxSize, ulong FlagsToBeSet, ulong FlagsToBeClear);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "prepare_PrintBeforeMF", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern int prepare_PrintBeforeMF(byte[] Buffer, int MaxSize);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "prepare_PrintMF", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern int prepare_PrintMF(byte[] Buffer, int MaxSize);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "prepare_SetParkingTicket", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern int prepare_SetParkingTicket(byte[] Buffer, int MaxSize, byte[] pCarIdentification);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "prepare_PrintTotalsAndPayments", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern int prepare_PrintTotalsAndPayments(byte[] Buffer, int MaxSize);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "prepare_VoidItem", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern int prepare_VoidItem(byte[] Buffer, int MaxSize, ushort Index, long ItemCount, byte ItemCountPrecision);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "prepare_Matrahsiz", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern int prepare_Matrahsiz(byte[] Buffer, int MaxSize, byte[] pTckNo, long MatrahsizAmount);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "prepare_Pretotal", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern int prepare_Pretotal(byte[] Buffer, int MaxSize);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "prepare_DisplayPaymentSummary", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern int prepare_DisplayPaymentSummary(byte[] Buffer, int MaxSize);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "prepare_Plus", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern int prepare_Plus(byte[] Buffer, int MaxSize, int Amount, UInt16 IndexOfItem);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "prepare_Minus", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern int prepare_Minus(byte[] Buffer, int MaxSize, int Amount, ushort IndexOfItem);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "prepare_Inc", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern int prepare_Inc(byte[] Buffer, int MaxSize, byte Rate, ushort IndexOfItem);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "prepare_Dec", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern int prepare_Dec(byte[] Buffer, int MaxSize, byte Rate, ushort IndexOfItem);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "prepare_VoidPayment", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern int prepare_VoidPayment(byte[] Buffer, int MaxSize, ushort Index);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "prepare_VoidAll", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern int prepare_VoidAll(byte[] Buffer, int MaxSize);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "prepare_JumpToECR", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern int prepare_JumpToECR(byte[] Buffer, int MaxSize, ulong JumpFlags);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "prepare_SetTaxFreeRefundAmount", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern int prepare_SetTaxFreeRefundAmount(byte[] Buffer, int MaxSize, int RefundAmount, ushort RefundAmountCurrency);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "prepare_Text", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern int prepare_Text(byte[] Buffer, int MaxSize, UInt32 TagId, byte[] pTitle, byte[] pText, byte[] pMask, byte[] pValue, int TimeoutInMiliseconds);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "prepare_Amount", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern int prepare_Amount(byte[] Buffer, int MaxSize, UInt32 TagId, byte[] pTitle, byte[] pText, byte[] pMask, byte[] pValue, int MaxLenOfValue, byte[] pSymbol, byte Align, int TimeoutInMiliseconds);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "prepare_Password", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern int prepare_Password(byte[] Buffer, int MaxSize, UInt32 TagId, byte[] pTitle, byte[] pText, byte[] pMask, byte[] pValue, UInt16 MaxLenOfValue, int TimeoutInMiliseconds);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "prepare_MsgBox", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern int prepare_MsgBox(byte[] Buffer, int MaxSize, UInt32 TagId, byte[] pTitle, byte[] pText, byte Icon, byte Button, int TimeoutInMiliseconds);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "FP3_SetuApplicationFunction", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 FP3_SetuApplicationFunction(UInt32 hInt, byte[] pUApplicationName, UInt32 UApplicationId, byte[] pFunctionName, UInt32 FunctionId, UInt32 FunctionVersion, UInt32 FunctionFlags, byte[] pCommandBuffer, UInt32 CommandLen, string szSupervisorPassword);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "FP3_GetDialogInput_Text", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 FP3_GetDialogInput_Text(UInt32 hInt, ref UInt32 pGL_Dialog_retcode, UInt32 TagId, byte[] pTitle, byte[] pText, byte[] pMask, byte[] pValue, int MaxLenOfValue, int TimeoutInMiliseconds);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "FP3_GetDialogInput_Date", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 FP3_GetDialogInput_Date(UInt32 hInt, ref UInt32 pGL_Dialog_retcode, UInt32 TagId, byte[] pTitle, byte[] pText, byte[] pMask, ref ST_DATE_TIME pValue, int MaxLenOfValue, int TimeoutInMiliseconds);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "FP3_GetDialogInput_Amount", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 FP3_GetDialogInput_Amount(UInt32 hInt, ref UInt32 pGL_Dialog_retcode, UInt32 TagId, byte[] pTitle, byte[] pText, byte[] pMask, byte[] pValue, int MaxLenOfValue, byte[] pSymbol, byte Align, int TimeoutInMiliseconds);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "FP3_GetDialogInput_MsgBox", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 FP3_GetDialogInput_MsgBox(UInt32 hInt, ref UInt32 pGL_Dialog_retcode, UInt32 TagId, byte[] pTitle, byte[] pText, byte Icon, byte Button, int TimeoutInMiliseconds);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "FP3_GetDialogInput_Password", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 FP3_GetDialogInput_Password(UInt32 hInt, ref UInt32 pGL_Dialog_retcode, UInt32 TagId, byte[] pTitle, byte[] pText, byte[] pMask, byte[] pValue, int MaxLenOfValue, int TimeoutInMiliseconds);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "FP3_OptionFlags", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 FP3_OptionFlags(UInt32 hInt, UInt64 hTrx, ref UInt64 pFlagsActive, UInt64 FlagsToBeSet, UInt64 FlagsToBeClear, int TimeoutInMiliseconds);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "FP3_SetTlvData", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 FP3_SetTlvData(UInt32 hInt, UInt32 Tag, byte[] pData, UInt16 Len);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "FP3_SendFrontStationPrint", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 FP3_SendFrontStationPrint(UInt32 hInt, UInt64 hTrx, byte[] pSendBuffer, Int16 SendLen, byte[] pReceiveBuffer, ref UInt16 ReceiveLen, int timeoutInMiliseconds);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "FP3_SendFrontStationPrintEx", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 FP3_SendFrontStationPrintEx(UInt32 hInt, UInt64 hTrx, byte[] pSendBuffer, Int16 SendLen, byte[] pReceiveBuffer, ref UInt16 ReceiveLen, int ReceiveTimeOut, int PrinterTimeOut);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "FiscalPrinter_SendFrontStationPrint", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 FiscalPrinter_SendFrontStationPrint(byte[] pSendBuffer, Int16 SendLen, byte[] pReceiveBuffer, ref UInt16 ReceiveLen, int timeoutInMiliseconds);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "FP3_IsGmpPairingDone", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 FP3_IsGmpPairingDone(UInt32 hInt);

        [DllImport("GmpSmartDLL.dll", EntryPoint = "FP3_FunctionLoadBackGroundHandleToFront", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 FP3_FunctionLoadBackGroundHandleToFront(UInt32 hInt, UInt64 hTrx, int timeoutInMiliseconds);

        [DllImport("sqlite3.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_open")]
        public static extern int sqlite3_open(string szFilename, out IntPtr pDb);

        [DllImport("sqlite3.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_close")]
        public static extern int sqlite3_close(IntPtr pDb);

        [DllImport("sqlite3.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_prepare_v2")]
        public static extern int sqlite3_prepare_v2(IntPtr pDb, string szSql, int nByte, out IntPtr ppStmpt, IntPtr pzTail);

        [DllImport("sqlite3.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_step")]
        public static extern int sqlite3_step(IntPtr stmHandle);

        [DllImport("sqlite3.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_finalize")]
        public static extern int sqlite3_finalize(IntPtr stmHandle);

        [DllImport("sqlite3.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_errmsg")]
        public static extern string sqlite3_errmsg(IntPtr db);

        [DllImport("sqlite3.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_column_count")]
        public static extern int sqlite3_column_count(IntPtr stmHandle);

        [DllImport("sqlite3.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_column_origin_name")]
        public static extern System.IntPtr sqlite3_column_origin_name(IntPtr stmHandle, int iCol);

        [DllImport("sqlite3.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_column_type")]
        public static extern int sqlite3_column_type(IntPtr stmHandle, int iCol);

        [DllImport("sqlite3.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_column_int")]
        public static extern int sqlite3_column_int(IntPtr stmHandle, int iCol);

        [DllImport("sqlite3.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_column_text")]
        public static extern System.IntPtr sqlite3_column_text(IntPtr stmHandle, int iCol);

        [DllImport("sqlite3.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_column_double")]
        public static extern double sqlite3_column_double(IntPtr stmHandle, int iCol);

        public static UInt32 FP3_FunctionCashierLogin(UInt32 hInt, int CashierIndex, string szCashierPassword)
        {
            byte[] CashierPassword = GMP_Tools.GetBytesFromString(szCashierPassword);
            return FP3_FunctionCashierLogin_WE(hInt, CashierIndex, CashierPassword);
        }

        public static UInt32 FP3_FunctionAddCashier(UInt32 hInt, ushort CashierIndex, string szCashierName, string szCashierPassword, string szSupervisorPassword)
        {
            byte[] CashierName = GMP_Tools.GetBytesFromString(szCashierName);
            byte[] CashierPassword = GMP_Tools.GetBytesFromString(szCashierPassword);
            byte[] SupervisorPassword = GMP_Tools.GetBytesFromString(szSupervisorPassword);

            return FP3_FunctionAddCashier_WE(hInt, CashierIndex, CashierName, CashierPassword, SupervisorPassword);
        }

        public static void SetIniFilePath(string szPath)
        {
            byte[] Path = GMP_Tools.GetBytesFromString(szPath);
            SetIniFilePath_WE(Path);
        }

        public static UInt32 FP3_Database_Open(UInt32 hInt, string szPath)
        {
            byte[] Path = GMP_Tools.GetBytesFromString(szPath);
            return FP3_Database_Open_WE(hInt, Path);
        }

        public static UInt32 FP3_FileSystem_DirChange(UInt32 hInt, string szPath)
        {
            byte[] Path = GMP_Tools.GetBytesFromString(szPath);
            return FP3_FileSystem_DirChange_WE(hInt, Path);
        }

        public static UInt32 FP3_FileSystem_FileRead(UInt32 hInt, string szFileName, int Offset, int Whence, byte[] Buffer, int MaxLen, ref short pReadLen)
        {
            byte[] FileName = GMP_Tools.GetBytesFromString(szFileName);
            return FP3_FileSystem_FileRead_WE(hInt, FileName, Offset, Whence, Buffer, MaxLen, ref pReadLen);
        }

        public static UInt32 FP3_SetPluDatabaseInfo(UInt32 hInt, string szPluDbName, short szPluDbType) // Without Encoding
        {
            byte[] PluDbName = GMP_Tools.GetBytesFromString(szPluDbName);
            return FP3_SetPluDatabaseInfo_WE(hInt, PluDbName, szPluDbType);
        }

        public static UInt32 Database_Query(string szSqlWord, ref ushort pNumberOfColomns)
        {
            byte[] SqlWord = GMP_Tools.GetBytesFromString(szSqlWord);
            return Database_Query_WE(SqlWord, ref pNumberOfColomns);
        }

        public static UInt32 FP3_Database_Query(UInt32 hInt, string szSqlWord, ref ushort pNumberOfColomns)
        {
            byte[] SqlWord = GMP_Tools.GetBytesFromString(szSqlWord);
            return FP3_Database_Query_WE(hInt, SqlWord, ref pNumberOfColomns);
        }

        public static int prepare_SetTaxFree(byte[] Buffer, int MaxSize, int Flag, string szName, string szSurname, string szIdentificationNo, string szCity, string szCountry)
        {
            byte[] Name = GMP_Tools.GetBytesFromString(szName);
            byte[] Surname = GMP_Tools.GetBytesFromString(szSurname);
            byte[] IdentificationNo = GMP_Tools.GetBytesFromString(szIdentificationNo);
            byte[] City = GMP_Tools.GetBytesFromString(szCity);
            byte[] Country = GMP_Tools.GetBytesFromString(szCountry);

            return prepare_SetTaxFree_WE(Buffer, MaxSize, Flag, Name, Surname, IdentificationNo, City, Country);
        }

        public static int prepare_KasaAvans(byte[] Buffer, int MaxSize, int Amount, string szCustomerName, string szTckn, string szVkn)
        {
            byte[] CustomerName = GMP_Tools.GetBytesFromString(szCustomerName);
            byte[] Tckn = GMP_Tools.GetBytesFromString(szTckn);
            byte[] Vkn = GMP_Tools.GetBytesFromString(szVkn);

            return prepare_KasaAvans(Buffer, MaxSize, Amount, CustomerName, Tckn, Vkn);
        }
    }

    class Defines
    {

        public const string m_deptIndCol = "Dept Index";
        public const string m_taxIndCol = "Tax Index";
        public const string m_nameCol = "Name";
        public const string m_unitCol = "Unit Type";
        public const string m_amountCol = "Amount";
        public const string m_currencyCol = "Currency";
        public const string m_limitAmountCol = "Limit Amount";
        public const string m_lunchCardCol = "Lunch Card";


        public const int TRAN_STATUS_FREE = 1;
        public const int TRAN_STATUS_RESERVED = 2;
        public const int TRAN_STATUS_SAVED = 3;
        public const int TRAN_STATUS_VOIDED = 4;
        public const int TRAN_STATUS_COMPLETED = 5;

        public const int LOYALTY_CUSTOMER_ID_TYPE_MOBILE_TEL = 1;
        public const int LOYALTY_CUSTOMER_ID_TYPE_MUSTERI_NO = 2;
        public const int LOYALTY_CUSTOMER_ID_TYPE_DIGER = 3;

        public const int TIMEOUT_DEFAULT = 10000;	// 10 seconds
        public const int TIMEOUT_CARD_TRANSACTIONS = 100000;	// 100 seconds
        public const int TIMEOUT_ECHO = 10000;	// 10 seconds
        public const int TIMEOUT_PRINT_MF = 20000;	// 20 seconds
        public const int TIMEOUT_DATABASE_EXECUTE = 20000;	// 20 seconds
        public const int MAX_UNIQUE_ID = 256;
        public const string DLL_VERSION_MIN = "1602030800";

        public const uint BANK_TRAN_FLAG_DO_NOT_ASK_FOR_MISSING_LOYALTY_POINT = 0x04000000;	    // 0x04000000 Do not ask for missing loyalty point
        public const uint BANK_TRAN_FLAG_ALL_INPUT_FROM_EXTERNAL_SYSTEM = 0x08000000;           // 0x08000000 All Input from ECR
        public const uint BANK_TRAN_FLAG_ASK_FOR_MISSING_REFUND_INPUTS = 0x10000000;	        // 0x10000000 Ask for Missing Refund Inputs
        public const uint BANK_TRAN_FLAG_LOYALTY_POINT_NOT_SUPPORTED_FOR_TRANS = 0x20000000;	// 0x20000000 Loyalty point not supported for transaction.
        public const uint BANK_TRAN_FLAG_ONLINE_FORCED_TRANSACTION = 0x40000000;	            // 0x40000000 Reserved for internal use.
        public const uint BANK_TRAN_FLAG_MANUAL_PAN_ENTRY_NOT_ALLOWED = 0x80000000;	            // 0x80000000 Manual pan entry not allowed for transaction.
        public const uint BANK_TRAN_FLAG_AUTHORISATION_FOR_INVOICE_PAYMENT = 0x02000000;
        public const uint BANK_TRAN_FLAG_SALE_WITHOUT_CAMPAIGN = 0x01000000;


        public const int EKU_SEEK_MODE_ALL_TYPE = 0xFF;

        public const int STANDART_BUFFER = 50000;
        public const int GMP_TICKET_BUFFER = 200000;

        public const int GMP_EXT_DEVICE_FILEDIR_BITMAP = 0xDFEDF8;
        public const int GMP_EXT_DEVICE_TAG_Z_NO = 0xDFED06;
        public const int GMP_EXT_DEVICE_TAG_TRAN_DB_NAME = 0xDFEE55;
        public const int GMP_EXT_DEVICE_FIS_LIMIT = 0xDFED54;
        public const int GMP_TAG_GROUP_OKC_DOVIZ_TABLOSU = 0xDF79;


        // Jump Flags for GMP3
        public const int GMP3_OPTION_RETURN_AFTER_SINGLE_PAYMENT = (1 << 1);
        public const int GMP3_OPTION_RETURN_AFTER_COMPLETE_PAYMENT = (1 << 2);
        public const int GMP3_OPTION_DONT_ALLOW_NEW_ITEM = (1 << 3);
        public const int GMP3_OPTION_DONT_ALLOW_VOID_ITEM = (1 << 4);
        public const int GMP3_OPTION_DONT_ALLOW_VOID_PAYMENT = (1 << 5);
        public const int GMP3_OPTION_CONTINUE_IN_OFFLINE_MODE = (1 << 6);
        public const int GMP3_OPTION_DONT_SEND_TRANSACTION_RESULT = (1 << 7);

        public const int GMP3_OPTION_DONT_ALLOW_NEW_PAYMENT_CASH_TL = (1 << 17);
        public const int GMP3_OPTION_DONT_ALLOW_NEW_PAYMENT_CASH_EXCHANGE = (1 << 18);
        public const int GMP3_OPTION_DONT_ALLOW_NEW_PAYMENT_BANKCARD = (1 << 19);
        public const int GMP3_OPTION_DONT_ALLOW_NEW_PAYMENT_YEMEKCEKI = (1 << 20);
        public const int GMP3_OPTION_DONT_ALLOW_NEW_PAYMENT_MOBILE = (1 << 21);
        public const int GMP3_OPTION_DONT_ALLOW_NEW_PAYMENT_HEDIYECEKI = (1 << 22);
        public const int GMP3_OPTION_DONT_ALLOW_NEW_PAYMENT_IKRAM = (1 << 23);
        public const int GMP3_OPTION_DONT_ALLOW_NEW_PAYMENT_ODEMESIZ = (1 << 24);
        public const int GMP3_OPTION_DONT_ALLOW_NEW_PAYMENT_KAPORA = (1 << 25);
        public const int GMP3_OPTION_DONT_ALLOW_NEW_PAYMENT_PUAN = (1 << 26);
        public const int GMP3_OPTION_DONT_ALLOW_NEW_PAYMENT = (GMP3_OPTION_DONT_ALLOW_NEW_PAYMENT_CASH_TL | GMP3_OPTION_DONT_ALLOW_NEW_PAYMENT_CASH_EXCHANGE | GMP3_OPTION_DONT_ALLOW_NEW_PAYMENT_BANKCARD | GMP3_OPTION_DONT_ALLOW_NEW_PAYMENT_YEMEKCEKI | GMP3_OPTION_DONT_ALLOW_NEW_PAYMENT_MOBILE | GMP3_OPTION_DONT_ALLOW_NEW_PAYMENT_HEDIYECEKI | GMP3_OPTION_DONT_ALLOW_NEW_PAYMENT_IKRAM | GMP3_OPTION_DONT_ALLOW_NEW_PAYMENT_ODEMESIZ | GMP3_OPTION_DONT_ALLOW_NEW_PAYMENT_KAPORA | GMP3_OPTION_DONT_ALLOW_NEW_PAYMENT_PUAN);

        public const int PAYMENT_OTHER_ALL = (int)(EPaymentTypes.PAYMENT_YEMEKCEKI | EPaymentTypes.PAYMENT_MOBILE | EPaymentTypes.PAYMENT_HEDIYE_CEKI | EPaymentTypes.PAYMENT_IKRAM | EPaymentTypes.PAYMENT_ODEMESIZ | EPaymentTypes.PAYMENT_KAPORA | EPaymentTypes.PAYMENT_GIDER_PUSULASI | EPaymentTypes.PAYMENT_PUAN | EPaymentTypes.PAYMENT_BANKA_TRANSFERI | EPaymentTypes.PAYMENT_CEK | EPaymentTypes.PAYMENT_ACIK_HESAP | EPaymentTypes.PAYMENT_DIGER);
        public const int PAYMENT_OTHER_REVERSE = (int)(EPaymentTypes.REVERSE_PAYMENT_YEMEKCEKI | EPaymentTypes.REVERSE_PAYMENT_MOBILE | EPaymentTypes.REVERSE_PAYMENT_HEDIYE_CEKI | EPaymentTypes.REVERSE_PAYMENT_PUAN | EPaymentTypes.REVERSE_PAYMENT_ACIK_HESAP);
        public const int PAYMENT_VAS_ALL = (int)(PAYMENT_OTHER_ALL | PAYMENT_OTHER_REVERSE);
        public const int PAYMENT_BANK_ALL = (int)(EPaymentTypes.PAYMENT_BANK_CARD | EPaymentTypes.REVERSE_PAYMENT_BANK_CARD_VOID | EPaymentTypes.REVERSE_PAYMENT_BANK_CARD_REFUND);

        public const int MAX_TAXRATE_COUNT = 8;
        public const int MAX_DEPARTMENT_COUNT = 12;
        public const int MAX_EXCHANGE_COUNT = 6;
        public const int MAX_CASHIER_COUNT = 4;
        public const int MAX_CINEMA_DEPARTMENT_COUNT = 8;


        public const int GMP3_FISCAL_PRINTER_MODE_REQ = 0xFF8A89;	/**< Request Msg Id for FISCAL PRINTER */
        public const int GMP3_FISCAL_PRINTER_MODE_REQ_E = 0xFF8B89;	/**< Request(Encrypted) Msg Id for FISCAL PRINTER */
        public const int GMP3_FISCAL_PRINTER_MODE_RES = 0xFF8E89;	/**< Response Msg Id for FISCAL PRINTER */
        public const int GMP3_FISCAL_PRINTER_MODE_RES_E = 0xFF8F89;	/**<  Response(Encrypted) Msg Id for FISCAL PRINTER */

        public const int GMP3_EXT_DEVICE_GET_DATA_REQ = 0xFF8A80;	    /**< Request Msg Id for GET DATA */
        public const int GMP3_EXT_DEVICE_GET_DATA_REQ_E = 0xFF8B80;	    /**< Request(Encrypted) Msg Id for GET DATA */
        public const int GMP3_EXT_DEVICE_GET_DATA_RES = 0xFF8E80;	    /**< Response Msg Id for GET DATA */
        public const int GMP3_EXT_DEVICE_GET_DATA_RES_E = 0xFF8F80;	    /**< Response(Encrypted) Msg Id for GET DATA  */
        public const int GMP_EXT_DEVICE_FLIGHT_MODE = 0xDFEE69;     /**< Flight Mode Tag,   (uint8 1 byte)*/
        public const int GMP_EXT_DEVICE_TICKET_TIMEOUT = 0xDFEE6A;     /**< Timeout between bank tickets,   (uint8 1 byte)*/
        public const int GMP_EXT_DEVICE_COMM_STATUS = 0xDFEE6B;     /**< Communication Status Gprs,Flight Mode, Ethernet*/
        public const int GMP_EXT_DEVICE_COMM_SCENARIO = 0xDFEE6C;		/**< 0XDFEE6C, Communication Scenario Gprs, Ethernet, Gprs&Ethernet */
        public const int GMP_EXT_DEVICE_STAND_BY_TIME = 0xDFEE6D;     /**< 0xDFEE6D, Set standby time value */

        public const int GMP_EXT_DEVICE_FISCAL_USAGE_INFO   = 0xDFEE75;     /**< 0xDFEE75 */
        public const int GMP_EXT_DEVICE_EKU_USAGE_INFO      = 0xDFEE76;		/**< 0xDFEE76 */

        public const byte GMP3_STATE_BIT_FLIGHT_MODE = (1 << 0); /**< State of flight mode*/
        public const byte GMP3_STATE_BIT_GPRS_CONNECTED = (1 << 1); /**<  State of GPRS connection*/
        public const byte GMP3_STATE_BIT_ETHERNET_CONNECTED = (1 << 2); /**<  State of Ethernet connection*/

        public const byte MAX_LOYALITY_TRANS_NUMBER = 8;		/**< ST_TICKET MAX LOYALTY CUSTOMER COUNT */

        public const int GMP3_OPTION_ECHO_PRINTER = (1 << 0);
        public const int GMP3_OPTION_ECHO_PAYMENT_DETAILS = (1 << 1);
        public const int GMP3_OPTION_ECHO_ITEM_DETAILS = (1 << 2);
        public const int GMP3_OPTION_NO_RECEIPT_LIMIT_CONTROL_FOR_ITEMS = (1 << 3);
        public const int GMP3_OPTION_DONOT_CONTROL_PAYMENTS_FOR_RECEIPT_CANCEL = (1 << 4);
        public const int GMP3_OPTION_GET_CONFIRMATION_FOR_PAYMENT_CANCEL = (1 << 5);
        public const int GMP3_OPTION_ECHO_LOYALTY_DETAILS = (1 << 6);

        public const int TRAN_RESULT_OK = 0x0000;
        public const int TRAN_RESULT_NOT_ALLOWED = 0x0001;
        public const int TRAN_RESULT_TIMEOUT = 0x0002;
        public const int TRAN_RESULT_USER_ABORT = 0x0004;
        public const int TRAN_RESULT_EKU_PROBLEM = 0x0008;
        public const int TRAN_RESULT_CONTINUE = 0x0010;
        public const int TRAN_RESULT_NO_PAPER = 0x0020;
        public const int APP_ERR_GMP3_INVALID_HANDLE = 2317;                      // Handle var fakat yanlış
        public const int APP_ERR_ALREADY_DONE = 2080;
        public const int APP_ERR_PAYMENT_NOT_SUCCESSFUL_AND_NO_MORE_ERROR_CODE = 2085;
        public const int APP_ERR_PAYMENT_NOT_SUCCESSFUL_AND_MORE_ERROR_CODE = 2086;
        public const int DLL_RETCODE_PORT_NOT_OPEN = 0xF000;
        public const int DLL_RETCODE_RECV_BUSY = 0xF01C;
        public const int APP_ERR_TICKET_HEADER_ALREADY_PRINTED = 2078;
        public const int APP_ERR_TICKET_HEADER_NOT_PRINTED = 2077;
        public const int APP_ERR_FIS_LIMITI_ASILAMAZ = 2067;
        public const int APP_ERR_KISIM_LIMITI_ASILAMAZ = 2016;
        public const int DLL_RETCODE_TIMEOUT = 0xF003;
        public const int APP_ERR_FILE_EOF = 2226;


        public const int SQLITE_OK = 0;		                //!< Successful result
        public const int SQLITE_ROW = 100;		            //!< SQLITE_step() has another row ready
        public const int SQLITE_DONE = 101;		            //!< SQLITE_step() has finished executing

        public const int SQLITE_INTEGER = 1;
        public const int SQLITE_FLOAT = 2;
        public const int SQLITE_TEXT = 3;
        public const int SQLITE_BLOB = 4;
        public const int SQLITE_NULL = 5;

        // Printer option definition
        public const int PS_24 = 0;
        public const int PS_12 = 1 << 0;
        public const int PS_32 = 1 << 1;
        public const int PS_48 = 1 << 2;
        public const int PS_BOLD = 1 << 3;
        public const int PS_CENTER = 1 << 4;
        public const int PS_RIGHT = 1 << 5;
        public const int PS_INVERTED = 1 << 6;
        public const int PS_UNIQUE_ID = 1 << 7;
        public const int PS_BARCODE = 1 << 8;
        public const int PS_ECR_TICKET_HEADER = 1 << 9;
        public const int PS_GRAPHIC = 1 << 10;
        public const int PS_QRCODE = 1 << 11;
        public const int PS_16 = 1 << 12;
        public const int PS_38 = 1 << 13;
        public const int PS_MULT2 = PS_12;
        public const int PS_MULT4 = PS_32;
        public const int PS_MULT8 = PS_48;
        //public const int PS_ECR_TICKET_ITEM = 1 << 14;  // DONT USE
        //public const int PS_NO_BOS = 1 << 15;
        public const int PS_ECR_TICKET_ITEM = 1 << 16;
        public const int PS_ECR_TICKET_COPY = 1 << 17;
        public const int PS_ECR_USER_MSG_BEFORE_HEADER = 1 << 18;
        public const int PS_ECR_USER_MSG_AFTER_TOTALS = 1 << 19;
        public const int PS_ECR_USER_MSG_BEFORE_MF = 1 << 20;
        public const int PS_ECR_USER_MSG_AFTER_MF = 1 << 21;
        public const int PS_NO_PAPER_CHECK = 1 << 22;
        public const int PS_FEED_LINE = 1 << 23;

        public const int ITEM_TYPE_FREE = 0;
        public const int ITEM_TYPE_DEPARTMENT = 1;
        public const int ITEM_TYPE_PLU = 2;
        public const int ITEM_TYPE_TICKET = 3;
        public const int ITEM_TYPE_MONEYCOLLECTION = 9;

        public const int DLL_RETCODE_FAIL = 0xF00B;

        public const int PAYMENT_SUBTYPE_PROCESS_ON_POS = 0x00000000;
        public const int PAYMENT_SUBTYPE_SALE = 0x00000001;
        public const int PAYMENT_SUBTYPE_INSTALMENT_SALE = 0x00000002;
        public const int PAYMENT_SUBTYPE_LOYALTY_PUAN = 0x00000003;
        public const int PAYMENT_SUBTYPE_ADVANCE_REFUND = 0x00000004;
        public const int PAYMENT_SUBTYPE_INSTALLMENT_REFUND = 0x00000005;
        public const int PAYMENT_SUBTYPE_REFERENCED_REFUND = 0x00000006;
        public const int PAYMENT_SUBTYPE_REFERENCED_REFUND_WITH_CARD = 0x00000007;
        public const int PAYMENT_SUBTYPE_REFERENCED_REFUND_WITHOUT_CARD = 0x00000008;

        public const int APP_ERR_FISCAL_INVALID_ENTRY = 2009;
        public const uint APP_ERR_PAYMENT_FOUND = 2069;                               // VoidAll: committed payment exists, must VoidPayment first
        public const uint APP_ERR_PAYMENT_CAN_NOT_VOID = 2357;                        // VoidAll after PrintBeforeMF — ticket in fiscal memory
        public const uint DLL_RETCODE_COMM_TIMEOUT = 0xF007;                          // TCP/IP level timeout on FP3_Payment
        public const uint APP_ERR_APL_CREDIT_CANNOT_BIGGER_THAN_REMAIN_AMOUNT = 2022; // Card/voucher pay > remaining; payment may have gone through
        public const uint DLL_RETCODE_PAIRING_REQUIRED = 0xF020;                      // GMP pairing lost; must call FP3_StartPairingInit
        public const uint APP_ERR_GMP3_Z_REQUIRED = 2417;                             // Z raporu alınmadan satış yapılamaz

        public const int FLAG_SETSCENARIO_ETHERNET = 1;
        public const int FLAG_SETSCENARIO_GPRS = 2;
        public const int FLAG_SETSCENARIO_ETHERNET_GPRS = 3;

    }
}

