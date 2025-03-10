﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Insolvency.CalculationsEngine.Redundancy.BL.DTOs.APPA;
using Insolvency.CalculationsEngine.Redundancy.BL.Services.Interfaces;
using Insolvency.CalculationsEngine.Redundancy.BL.Calculations.APPA.Extensions;
using Insolvency.CalculationsEngine.Redundancy.Common.Extensions;
using Microsoft.Extensions.Options;
using Insolvency.CalculationsEngine.Redundancy.Common.ConfigLookups;
using Insolvency.CalculationsEngine.Redundancy.BL.Calculations.RedundancyPaymentCalculation.Extensions;
using Insolvency.CalculationsEngine.Redundancy.BL.Serializer.Extensions;

namespace Insolvency.CalculationsEngine.Redundancy.BL.Services.Implementations
{
    public class ArrearsOfPayCalculationsService : IArrearsOfPayCalculationsService
    {
        public async Task<ArrearsOfPayResponseDTO> PerformCalculationAsync(
            List<ArrearsOfPayCalculationRequestModel> data, string inputSource, IOptions<ConfigLookupRoot> options, TraceInfo traceInfo = null)
        {
            var responses = new List<ArrearsOfPayResponseDTO>();

            foreach (var request in data.Where(x => x.InputSource == inputSource))
                responses.Add(await PerformCalculationAsync(request, options, traceInfo));

            return await responses.MergeWeeklyResults(inputSource, options);
        }

        public async Task<ArrearsOfPayResponseDTO> PerformCalculationAsync(
            ArrearsOfPayCalculationRequestModel data, IOptions<ConfigLookupRoot> options, TraceInfo traceInfo = null)
        {
            var calculationResult = new ArrearsOfPayResponseDTO();
            var weeklyresult = new List<ArrearsOfPayWeeklyResult>();
            int weekNumber = 1;
            var totalDays = 0.00m;

            var statutoryMax = ConfigValueLookupHelper.GetStatutoryMax(options, data.InsolvencyDate);

            var relevantNoticeDate = await data.DateNoticeGiven.GetRelevantNoticeDate(data.DismissalDate);
            var noticeEntitlementWeeks = await data.EmploymentStartDate.GetNoticeEntitlementWeeks(relevantNoticeDate);    //not adjusted start date      
            var projectedNoticeEndDate = await relevantNoticeDate.GetProjectedNoticeDate(noticeEntitlementWeeks);


            var adjustedPeriodFrom = await data.UnpaidPeriodFrom.Date.GetAdjustedPeriodFromAsync(data.InsolvencyDate.Date);
            var adjustedPeriodTo = await data.UnpaidPeriodTo.Date.GetAdjustedPeriodToAsync(data.InsolvencyDate.Date, data.DismissalDate.Date);

            DateTime extendedAdjustedPeriodTo = adjustedPeriodTo;
            if (extendedAdjustedPeriodTo.DayOfWeek != (DayOfWeek)data.PayDay)
                extendedAdjustedPeriodTo = adjustedPeriodTo.AddDays(7);
            var payDays = await adjustedPeriodFrom.Date.GetDaysInRange(extendedAdjustedPeriodTo.Date, (DayOfWeek)data.PayDay);

            decimal adjustedWeeklyWage = await data.WeeklyWage.GetAdjustedWeeklyWageAsync(data.ShiftPattern, data.UnpaidPeriodFrom, data.UnpaidPeriodTo, data.ApClaimAmount);
            decimal WeeklyWageBetweenNoticeGivenAndNoticeEnd = decimal.Zero;

            DateTime prefPeriodStartDate = data.InsolvencyDate.Date.AddMonths(-4);
            prefPeriodStartDate = (prefPeriodStartDate <= data.EmploymentStartDate.Date) ? data.EmploymentStartDate.Date : prefPeriodStartDate.Date;
            DateTime prefPeriodEndDate = (data.DismissalDate < data.InsolvencyDate) ? data.DismissalDate.Date : data.InsolvencyDate.Date;

            //step through paydaysCollection
            foreach (var payWeekEnd in payDays)
            {
                var employmentDays = 0;
                var employmentDaysBetweenNoticeGivenAndNoticeEndDate = 0;
                var maximumEntitlement = 0.0m;
                var employmentDaysInPrefPeriod = 0;
                var employmentDaysInPrefPeriodPostDNG = 0;
                var maximumEntitlementInPrefPeriod = 0.0m;
                var employerEntitlementInPrefPeriod = 0.0m;

                for (int j = 6; j >= 0; j--)
                {
                    DateTime date = payWeekEnd.AddDays(-j);

                    //is this day a working day?
                    if (await date.IsEmploymentDay(data.ShiftPattern))
                    {

                        if (date >= adjustedPeriodFrom.Date && date <= adjustedPeriodTo.Date)
                        {
                            if (date > data.DateNoticeGiven.Date && date <= projectedNoticeEndDate.Date)
                            {
                                employmentDaysBetweenNoticeGivenAndNoticeEndDate++;

                                if (date >= prefPeriodStartDate && date <= prefPeriodEndDate)
                                    employmentDaysInPrefPeriodPostDNG++;
                            }

                            else
                            {
                                employmentDays++;

                                if (date >= prefPeriodStartDate && date <= prefPeriodEndDate)
                                    employmentDaysInPrefPeriod++;
                            }
                        }
                    }
                }

                var weekStartDate = payWeekEnd.AddDays(-6);
                var maximumDays = await weekStartDate.GetNumDaysInIntersectionOfTwoRangesWithLimit(payWeekEnd, data.EmploymentStartDate.Date, data.DismissalDate.Date, data.DateNoticeGiven.Date, data.InsolvencyDate.Date);
                var maximumDaysInPrefPeriod = await weekStartDate.GetNumDaysInIntersectionOfTwoRangesWithLimit(payWeekEnd, prefPeriodStartDate, prefPeriodEndDate, data.DateNoticeGiven.Date, data.InsolvencyDate.Date);

                //calculate Employer Liability for week
                var employerEntitlement = adjustedWeeklyWage / data.ShiftPattern.Count * employmentDays +
                                            WeeklyWageBetweenNoticeGivenAndNoticeEnd / data.ShiftPattern.Count * employmentDaysBetweenNoticeGivenAndNoticeEndDate;

                employerEntitlementInPrefPeriod = adjustedWeeklyWage / data.ShiftPattern.Count * employmentDaysInPrefPeriod;
                employerEntitlementInPrefPeriod = Math.Round(employerEntitlementInPrefPeriod, 2);

                //calculate Statutory Maximum liability for week
                maximumEntitlementInPrefPeriod = Math.Round((statutoryMax / 7 * maximumDaysInPrefPeriod), 2);
                maximumEntitlement = statutoryMax / 7 * maximumDays;

                var grossEntitlement = Math.Min(maximumEntitlement, employerEntitlement);

                var taxRate = ConfigValueLookupHelper.GetTaxRate(options, DateTime.Now);
                var taxDeducated = Math.Round(await grossEntitlement.GetTaxDeducted(taxRate, data.IsTaxable), 2);

                var niThreshold = ConfigValueLookupHelper.GetNIThreshold(options, DateTime.Now);
                var niRate = ConfigValueLookupHelper.GetNIRate(options, DateTime.Now);
                var niDeducted = Math.Round(await grossEntitlement.GetNIDeducted(niThreshold, niRate, data.IsTaxable), 2);

                grossEntitlement = Math.Round(grossEntitlement, 2);
                var netLiability = await grossEntitlement.GetNetLiability(taxDeducated, niDeducted);

                weeklyresult.Add(new ArrearsOfPayWeeklyResult
                {
                    WeekNumber = weekNumber++,
                    PayDate = payWeekEnd,
                    ApPayRate = Math.Round(adjustedWeeklyWage, 2),
                    EmployerEntitlement = Math.Round(employerEntitlement, 2),
                    MaximumEntitlement = Math.Round(maximumEntitlement, 2),
                    GrossEntitlement = grossEntitlement,
                    IsTaxable = data.IsTaxable,
                    TaxDeducted = taxDeducated,
                    NIDeducted = niDeducted,
                    NetEntitlement = netLiability,
                    MaximumDays = maximumDays,
                    EmploymentDays = employmentDays + employmentDaysInPrefPeriodPostDNG,
                    MaximumEntitlementIn4MonthPeriod = maximumEntitlementInPrefPeriod,
                    EmployerEntitlementIn4MonthPeriod = employerEntitlementInPrefPeriod,
                    GrossEntitlementIn4Months = Math.Min(maximumEntitlementInPrefPeriod, employerEntitlementInPrefPeriod)
                });
                totalDays += employmentDays + employmentDaysInPrefPeriodPostDNG;
            } //outter loop for paydays collection

            calculationResult.InputSource = data.InputSource;
            calculationResult.StatutoryMax = Math.Round(statutoryMax, 2);
            calculationResult.DngApplied = (adjustedPeriodTo > data.DateNoticeGiven.Date);
            calculationResult.RunNWNP = (adjustedPeriodTo > data.DateNoticeGiven.Date);
            calculationResult.WeeklyResult = weeklyresult;
            traceInfo?.Dates.Add(new TraceInfoDate
            {
                StartDate = data.UnpaidPeriodFrom,
                EndDate = data.UnpaidPeriodTo
            });
            if (traceInfo != null)
                traceInfo.NumberOfDays = totalDays;

            return await Task.FromResult(calculationResult);
        }
    }
}